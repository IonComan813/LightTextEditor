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
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
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
}
