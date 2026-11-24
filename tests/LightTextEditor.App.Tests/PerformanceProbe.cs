using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>
/// 资源占用测量（不是断言测试）：只有设置了环境变量 LTE_PERF=1 时才运行，结果（制表符分隔）追加到
/// LTE_PERF_OUT 指定的文件。每个场景要在全新的进程中单独运行，例如：
///   LTE_PERF=1 LTE_PERF_OUT=perf.tsv dotnet test tests/LightTextEditor.App.Tests -c Release --filter "FullyQualifiedName~PerformanceProbe.Startup"
/// 无头环境（软件渲染、没有真实窗口）与 Windows 上的实际数值不同，只用于修改前后的对比。
/// </summary>
public class PerformanceProbe
{
    private static bool Enabled => Environment.GetEnvironmentVariable("LTE_PERF") == "1";

    private static void Report(string scenario, string metric, double value, string unit)
    {
        var line = $"{scenario}\t{metric}\t{value:0.##}\t{unit}";
        var output = Environment.GetEnvironmentVariable("LTE_PERF_OUT");
        if (string.IsNullOrEmpty(output)) Console.WriteLine(line);
        else File.AppendAllText(output, line + Environment.NewLine);
    }

    private static void ReportMemory(string scenario)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Report(scenario, "托管堆", GC.GetTotalMemory(false) / 1024.0 / 1024.0, "MB");
        Report(scenario, "已加载程序集", AppDomain.CurrentDomain.GetAssemblies().Length, "个");
    }

    private static MainWindow CreateWindow(out string library)
    {
        library = Path.Combine(TestAppBuilder.Root, "perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false,
            WordWrap = Environment.GetEnvironmentVariable("PERF_WRAP") != "0" };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task SettleAsync()
    {
        // 等后台着色（TextMate）完成
        for (int i = 0; i < 10; i++) { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task Startup_OpenTextFile()
    {
        if (!Enabled) return;
        var sw = Stopwatch.StartNew();
        var window = CreateWindow(out var library);
        Report("启动", "创建并显示窗口", sw.Elapsed.TotalMilliseconds, "ms");

        var path = Path.Combine(library, "notes.txt");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"第 {i} 行：普通的纯文本笔记内容。")));
        sw.Restart();
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        Report("启动", "打开 2000 行 .txt", sw.Elapsed.TotalMilliseconds, "ms");
        ReportMemory("启动并打开 .txt");
    }

    [AvaloniaFact]
    public void Typing()
    {
        if (!Enabled) return;
        var window = CreateWindow(out _);
        for (int i = 0; i < 4; i++) window.NewDocument();
        window.ActiveTab!.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 50; i++) { window.KeyTextInput("a"); Dispatcher.UIThread.RunJobs(); } // 预热

        const int keys = 1000;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < keys; i++)
        {
            window.KeyTextInput(i % 40 == 39 ? "\n" : "字");
            Dispatcher.UIThread.RunJobs();
        }
        Report("输入", "每次按键分配内存（不含渲染）", (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0 / keys, "KB");
    }

    /// <summary>同时打开 PERF_COUNT 个（默认 3）后缀为 PERF_EXT（默认 .md）的文件。</summary>
    [AvaloniaFact]
    public async Task OpenFiles()
    {
        if (!Enabled) return;
        var ext = Environment.GetEnvironmentVariable("PERF_EXT") ?? ".md";
        int count = int.Parse(Environment.GetEnvironmentVariable("PERF_COUNT") ?? "3");
        var window = CreateWindow(out var library);
        var paths = Enumerable.Range(1, count).Select(n => Path.Combine(library, $"file{n}{ext}")).ToList();
        foreach (var path in paths)
            File.WriteAllText(path, "# 标题\n\n" + string.Join("\n", Enumerable.Range(1, 300).Select(i => $"第 {i} 行 **粗体** x{i} = {i}")));
        var sw = Stopwatch.StartNew();
        await window.OpenFilesAsync(paths);
        Dispatcher.UIThread.RunJobs();
        Report($"打开 {count} 个 {ext}", "耗时", sw.Elapsed.TotalMilliseconds, "ms");
        await SettleAsync();
        ReportMemory($"打开 {count} 个 {ext}");
    }

    [AvaloniaFact]
    public async Task MarkdownPreview()
    {
        if (!Enabled) return;
        var window = CreateWindow(out var library);
        var path = Path.Combine(library, "note.md");
        File.WriteAllText(path, "# 标题\n\n" + string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"第 {i} 段，**粗体** 和 `代码`。")));
        await window.OpenFilesAsync([path]);
        await SettleAsync();
        ReportMemory("打开 .md（未预览）");

        var sw = Stopwatch.StartNew();
        window.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Report("预览", "第一次切换到预览", sw.Elapsed.TotalMilliseconds, "ms");
        ReportMemory("预览后");
    }

    /// <summary>打开、预览再关闭几个文件之后，后台回收（与 BackgroundMode 相同的做法）能还给系统多少内存。</summary>
    [AvaloniaFact]
    public async Task ReleaseMemoryAfterClosingTabs()
    {
        if (!Enabled) return;
        var window = CreateWindow(out var library);
        string[] langs = ["python", "csharp", "javascript", "rust", "go", "java", "cpp", "html", "css", "sql"];
        var paths = new List<string>();
        for (int n = 0; n < 6; n++)
        {
            var path = Path.Combine(library, $"note{n}.md");
            File.WriteAllText(path, "# 标题\n\n" + string.Join("\n", langs.Select(l => $"```{l}\nx = 1\n```\n")) +
                string.Join("\n\n", Enumerable.Range(1, 300).Select(i => $"第 {i} 段，**粗体**、`代码`、$x^{i}$。")));
            paths.Add(path);
        }
        await window.OpenFilesAsync(paths);
        foreach (var tab in window.AllTabs().Where(t => t.Doc.FilePath is not null).ToList())
        {
            window.Select(tab);
            window.TogglePreview();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        await SettleAsync();
        foreach (var tab in window.AllTabs().ToList()) await window.CloseTabAsync(tab);
        await SettleAsync();

        static double PrivateMb() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / 1048576.0; }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Report("关闭标签后", "私有内存（普通回收后）", PrivateMb(), "MB");
        Report("关闭标签后", "GC 已提交内存", GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0, "MB");
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Report("后台回收后", "私有内存", PrivateMb(), "MB");
        Report("后台回收后", "GC 已提交内存", GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0, "MB");
    }

    /// <summary>
    /// 打开约 3.6 MB 的 JSON（PERF_SHAPE=minified：整个文件只有一行；pretty：格式化成多行），
    /// 测量打开并显示、输入、翻页的耗时（主线程被占用的时间）。
    /// </summary>
    [AvaloniaFact]
    public async Task LargeFile()
    {
        if (!Enabled) return;
        var shape = Environment.GetEnvironmentVariable("PERF_SHAPE") ?? "minified";
        var window = CreateWindow(out var library);
        // 与 Windows 上的默认字体（Consolas + 微软雅黑）相当：中文由字体列表里的中文字体显示，不走系统字体回退
        App.Settings.EditorFontFamily = "DejaVu Sans Mono, WenQuanYi Zen Hei";
        var ext = Environment.GetEnvironmentVariable("PERF_EXT") ?? ".json";
        var path = Path.Combine(library, "big" + ext);
        File.WriteAllText(path, LargeJson(shape == "pretty"));
        var scenario = $"大文件 {shape}{ext} 换行={App.Settings.WordWrap} {new FileInfo(path).Length / 1048576.0:0.00} MB";

        var sw = Stopwatch.StartNew();
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Report(scenario, "打开并显示", sw.Elapsed.TotalMilliseconds, "ms");

        // PERF_SETTLE_MS：先等这么久（让后台的语法分析做完）再测输入
        if (int.TryParse(Environment.GetEnvironmentVariable("PERF_SETTLE_MS"), out var settle))
            for (var until = DateTime.UtcNow.AddMilliseconds(settle); DateTime.UtcNow < until;) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }

        var editor = window.ActiveTab!.Editor;
        editor.TextArea.Focus();
        editor.CaretOffset = Math.Min(editor.Document.TextLength, int.Parse(Environment.GetEnvironmentVariable("PERF_CARET") ?? "1000"));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        sw.Restart();
        const int keys = 10;
        for (int i = 0; i < keys; i++)
        {
            window.KeyTextInput("a");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        Report(scenario, "每次按键", sw.Elapsed.TotalMilliseconds / keys, "ms");

        sw.Restart();
        const int pages = 10;
        for (int i = 0; i < pages; i++)
        {
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.PageDown, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        Report(scenario, "每次翻页", sw.Elapsed.TotalMilliseconds / pages, "ms");

        sw.Restart();
        editor.ScrollToHorizontalOffset(20000);
        editor.ScrollToVerticalOffset(editor.ExtentHeight / 2);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Report(scenario, "滚动到中间", sw.Elapsed.TotalMilliseconds, "ms");

        await SettleAsync();
        ReportMemory(scenario);
    }

    private static bool Ascii => Environment.GetEnvironmentVariable("PERF_ASCII") == "1";

    private static int Records => int.Parse(Environment.GetEnvironmentVariable("PERF_RECORDS") ?? "21000");

    /// <summary>约 3.6 MB 的 JSON（PERF_RECORDS 个对象的数组）。</summary>
    internal static string LargeJson(bool pretty)
    {
        var sb = new System.Text.StringBuilder();
        string nl = pretty ? "\n" : "", ind = pretty ? "  " : "", sp = pretty ? " " : "";
        sb.Append('[').Append(nl);
        for (int i = 0; i < Records; i++)
        {
            if (i > 0) sb.Append(',').Append(nl);
            sb.Append(ind).Append('{').Append(nl)
              .Append(ind).Append(ind).Append($"\"id\":{sp}{i},").Append(nl)
              .Append(ind).Append(ind).Append($"\"name\":{sp}\"{(Ascii ? "User" : "用户")}{i} user_{i}\",").Append(nl)
              .Append(ind).Append(ind).Append($"\"email\":{sp}\"user{i}@example.com\",").Append(nl)
              .Append(ind).Append(ind).Append($"\"tags\":{sp}[\"alpha\",{sp}\"beta\",{sp}\"gamma\"],").Append(nl)
              .Append(ind).Append(ind).Append($"\"score\":{sp}{i * 3.14:0.00},").Append(nl)
              .Append(ind).Append(ind).Append($"\"active\":{sp}{(i % 2 == 0 ? "true" : "false")}").Append(nl)
              .Append(ind).Append('}');
        }
        sb.Append(nl).Append(']');
        return sb.ToString();
    }
}
