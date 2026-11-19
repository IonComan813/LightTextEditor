using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class CenteredLayoutTests
{
    [AvaloniaFact]
    public async Task CenteredLayout_ShowsEditorAndPreviewAsCenteredPage()
    {
        var library = Path.Combine(TestAppBuilder.Root, "center-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, CenteredLayout = true, WordWrap = false };
        var window = new MainWindow { Width = 1400, Height = 600 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "a.md");
        File.WriteAllText(path, "# 标题\n\n" + new string('长', 400));
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        var editor = tab.Editor;

        // 页面宽 860，居中
        Assert.Equal(860, editor.Bounds.Width, 0);
        double left = editor.TranslatePoint(default, window)!.Value.X;
        Assert.InRange(left, (window.Bounds.Width - 860) / 2 - 1, (window.Bounds.Width - 860) / 2 + 1);
        // 不自动换行：长行在页面里横向滚动
        Assert.True(editor.TextArea.TextView.DocumentHeight > 0);
        Assert.True(editor.ExtentWidth > editor.ViewportWidth);

        // 页面两侧的空白处滚动滚轮，页面跟着滚动
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 300)));
        tab.Doc.Reload();
        Dispatcher.UIThread.RunJobs();
        window.MouseWheel(new Point(40, 300), new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.VerticalOffset > 0, $"offset={editor.VerticalOffset}");

        // 缩放时页面跟着变宽
        window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, "+");
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(editor.Bounds.Width, 860 * 1.1 - 1, 860 * 1.1 + 1);

        // 预览同样是居中的页面
        window.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(tab.Preview!.Bounds.Width, 860 * 1.1 - 1, 860 * 1.1 + 1);

        // 关闭居中布局：铺满
        window.ViewModel.CenteredLayout = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(window.Bounds.Width, tab.Preview.Bounds.Width, 0);
        Assert.False(App.Settings.CenteredLayout);
    }

    private static async Task<MainWindow> OpenAsync(AppSettings settings, params (string Name, string Text)[] files)
    {
        var library = Path.Combine(TestAppBuilder.Root, "page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        settings.SampleNoteCreated = true;
        settings.LibraryPath = library;
        settings.RestoreSession = false;
        App.Settings = settings;
        var window = new MainWindow { Width = 1600, Height = 600 };
        window.Initialize([]);
        window.Show();
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(library, name), text);
            await window.OpenFilesAsync([Path.Combine(library, name)]);
        }
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task NewTab_WrapsImmediately_WhenWordWrapIsOn()
    {
        var window = await OpenAsync(new AppSettings { WordWrap = true }, ("a.txt", new string('字', 500)));
        var editor = window.ActiveTab!.Editor;
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, editor.HorizontalScrollBarVisibility);
        Assert.True(editor.ExtentWidth <= editor.ViewportWidth + 1);
        Assert.True(editor.TextArea.TextView.VisualLines[0].TextLines.Count > 1);
    }

    [AvaloniaFact]
    public async Task Markdown_IsLimitedToPageWidth_OnTheLeft_TextFillsTheWindow()
    {
        var long1 = new string('字', 500);
        var window = await OpenAsync(new AppSettings { WordWrap = true },
            ("a.txt", long1), ("b.md", "# 标题\n\n" + long1 + "\n\n> [!NOTE]\n> 提示\n\n```\ncode\n```\n"));
        var md = window.ActiveTab!;
        // Markdown 源码：编辑器铺满（滚动条在窗口右边），但换行宽度为页面宽度
        Assert.Equal(window.Bounds.Width, md.Editor.Bounds.Width, 0);
        Assert.InRange(md.Editor.ViewportWidth, 800, 861);

        // Markdown 预览：内容靠左，宽度不超过页面宽度（代码块不会拉到最右边）
        window.TogglePreview();
        for (int i = 0; i < 6; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        var block = md.Preview!.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.CodeBlock>().Single();
        var blockLeft = block.TranslatePoint(default, window)!.Value.X;
        Assert.True(blockLeft < 60);
        Assert.True(blockLeft + block.Bounds.Width <= 861);
        Assert.True(block.IsCodeWrapped); // 代码块的换行只由块上的按钮控制，与“查看 → 自动换行”无关
        window.ViewModel.WordWrap = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(block.IsCodeWrapped);

        // 纯文本铺满窗口
        window.Select(window.AllTabs().First(t => t.Doc.Extension == ".txt"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.AllTabs().First(t => t.Doc.Extension == ".txt").Editor.ViewportWidth > 1400);
    }

    [AvaloniaFact]
    public async Task Markdown_WithoutWordWrap_LongLinesUseFullWidthAndScroll()
    {
        var line = string.Concat(Enumerable.Range(0, 120).Select(i => $"第{i}段"));
        var window = await OpenAsync(new AppSettings { WordWrap = false }, ("b.md", "# 标题\n\n" + line + "\n"));
        var editor = window.ActiveTab!.Editor;
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        // 不自动换行时不限制宽度：长行用整个窗口宽度显示，其余部分可以横向滚动看到
        Assert.Equal(default, editor.Padding);
        Assert.True(editor.ViewportWidth > 1400);
        Assert.True(editor.ExtentWidth > editor.ViewportWidth);

        // 打开自动换行：按页面宽度换行；再关掉：恢复整个窗口宽度
        window.ViewModel.WordWrap = true;
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(editor.ViewportWidth, 800, 861);
        window.ViewModel.WordWrap = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.ViewportWidth > 1400);
    }
}
