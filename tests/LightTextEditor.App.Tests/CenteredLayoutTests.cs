using Avalonia;
using Avalonia.Controls;
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
    private const double Page = MainWindow.PageWidth;   // 794：A4 纸宽
    private const double Margin = MainWindow.PageMargin; // 48：A4 窄页边距

    private static async Task<MainWindow> OpenAsync(AppSettings settings, double width, params (string Name, string Text)[] files)
    {
        var library = Path.Combine(TestAppBuilder.Root, "page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        settings.SampleNoteCreated = true;
        settings.LibraryPath = library;
        App.Settings = settings;
        var window = new MainWindow { Width = width, Height = 600 };
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

    private static DocumentTab TabOf(MainWindow window, string extension) =>
        window.AllTabs().First(t => t.Doc.Extension == extension);

    [AvaloniaFact]
    public async Task CenteredLayout_ShowsTextAsCenteredA4Page()
    {
        var window = await OpenAsync(new AppSettings { CenteredLayout = true, WordWrap = false }, 1400,
            ("a.txt", new string('长', 400)));
        var tab = window.ActiveTab!;
        var editor = tab.Editor;

        // 页面宽 794（A4），居中；代码和纯文本不留页边距
        Assert.Equal(Page, editor.Bounds.Width, 0);
        double left = editor.TranslatePoint(default, window)!.Value.X;
        Assert.InRange(left, (window.Bounds.Width - Page) / 2 - 1, (window.Bounds.Width - Page) / 2 + 1);
        Assert.Equal(default, editor.Padding);
        // 不自动换行：长行在页面里横向滚动
        Assert.True(editor.ExtentWidth > editor.ViewportWidth);

        // 页面两侧的空白处滚动滚轮，页面跟着滚动
        File.WriteAllText(tab.Doc.FilePath!, string.Join("\n", Enumerable.Range(1, 300)));
        tab.Doc.Reload();
        Dispatcher.UIThread.RunJobs();
        window.MouseWheel(new Point(40, 300), new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.VerticalOffset > 0, $"offset={editor.VerticalOffset}");

        // 缩放时页面跟着变宽
        window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, "+");
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(editor.Bounds.Width, Page * 1.1 - 1, Page * 1.1 + 1);

        // 关闭居中布局：铺满
        window.ViewModel.CenteredLayout = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(window.Bounds.Width, editor.Bounds.Width, 0);
        Assert.False(App.Settings.CenteredLayout);
    }

    [AvaloniaFact]
    public async Task NewTab_WrapsImmediately_WhenWordWrapIsOn()
    {
        var window = await OpenAsync(new AppSettings { WordWrap = true }, 1600, ("a.txt", new string('字', 500)));
        var editor = window.ActiveTab!.Editor;
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, editor.HorizontalScrollBarVisibility);
        Assert.True(editor.ExtentWidth <= editor.ViewportWidth + 1);
        Assert.True(editor.TextArea.TextView.VisualLines[0].TextLines.Count > 1);
    }

    [AvaloniaFact]
    public async Task Markdown_IsAlwaysCenteredAndWrapped_WithPageMargins()
    {
        var long1 = new string('字', 500);
        var line = string.Concat(Enumerable.Range(0, 120).Select(i => $"第{i}段"));
        // 两项设置都关着：代码和纯文本铺满、不换行；Markdown 仍然居中、换行，只有预览留页边距
        var window = await OpenAsync(new AppSettings { CenteredLayout = false, WordWrap = false }, 1600,
            ("a.txt", long1), ("b.md", "# 标题\n\n" + line + "\n\n> [!NOTE]\n> 提示\n\n```\ncode\n```\n"));
        var vm = window.ViewModel;
        var wrapItem = window.FindControl<MenuItem>("WordWrapMenuItem")!;
        var centerItem = window.FindControl<MenuItem>("CenteredLayoutMenuItem")!;
        var md = window.ActiveTab!;

        // 菜单项打勾且不可用
        Assert.True(wrapItem.IsChecked);
        Assert.True(centerItem.IsChecked);
        Assert.False(wrapItem.IsEnabled);
        Assert.False(centerItem.IsEnabled);

        // 源码：A4 宽的居中页面，不留页边距，长行自动换行
        Assert.Equal(Page, md.Editor.Bounds.Width, 0);
        double pageLeft = md.Editor.TranslatePoint(default, window)!.Value.X;
        Assert.InRange(pageLeft, (window.Bounds.Width - Page) / 2 - 1, (window.Bounds.Width - Page) / 2 + 1);
        Assert.Equal(default, md.Editor.Padding);
        Assert.True(md.Editor.ExtentWidth <= md.Editor.ViewportWidth + 1);
        Assert.Contains(md.Editor.TextArea.TextView.VisualLines, l => l.TextLines.Count > 1);

        // Alt+Z 不改设置（Markdown 总是换行）
        window.KeyPress(Key.Z, RawInputModifiers.Alt, PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();
        Assert.False(App.Settings.WordWrap);
        Assert.True(md.Editor.WordWrap);

        // 预览：同样的页面，内容在左右页边距之内
        window.TogglePreview();
        for (int i = 0; i < 6; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(Page, md.Preview!.Bounds.Width, 0);
        var block = md.Preview.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.CodeBlock>().Single();
        var blockLeft = block.TranslatePoint(default, window)!.Value.X;
        Assert.InRange(blockLeft, pageLeft + Margin - 1, pageLeft + Margin + 1);
        Assert.InRange(blockLeft + block.Bounds.Width, pageLeft + Page - Margin - 3, pageLeft + Page - Margin + 1);
        Assert.True(block.IsCodeWrapped); // 代码块的换行只由块上的按钮控制

        // 缩放：页面和页边距一起放大
        window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, "+");
        for (int i = 0; i < 3; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.InRange(md.Preview.Bounds.Width, Page * 1.1 - 1, Page * 1.1 + 1);
        window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.Digit0, "0");
        Dispatcher.UIThread.RunJobs();

        // 纯文本：按设置铺满窗口、不换行，菜单项可用、不打勾
        window.Select(TabOf(window, ".txt"));
        Dispatcher.UIThread.RunJobs();
        var txt = TabOf(window, ".txt").Editor;
        Assert.Equal(window.Bounds.Width, txt.Bounds.Width, 0);
        Assert.Equal(default, txt.Padding);
        Assert.False(txt.WordWrap);
        Assert.True(wrapItem.IsEnabled);
        Assert.True(centerItem.IsEnabled);
        Assert.False(wrapItem.IsChecked);
        Assert.False(centerItem.IsChecked);

        // 在纯文本标签上打开设置，再切回 Markdown：仍然居中、换行；关掉也不影响 Markdown
        vm.IsWordWrapOn = true;
        vm.IsCenteredLayoutOn = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(App.Settings.WordWrap);
        Assert.True(App.Settings.CenteredLayout);
        Assert.Equal(Page, txt.Bounds.Width, 0);
        vm.IsWordWrapOn = false;
        vm.IsCenteredLayoutOn = false;
        window.Select(md);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Page, md.Preview.Bounds.Width, 0);
        Assert.True(md.Editor.WordWrap);
    }

    [AvaloniaFact]
    public async Task RenamingToMarkdown_SwitchesToPageLayout()
    {
        var window = await OpenAsync(new AppSettings { CenteredLayout = false, WordWrap = false }, 1600,
            ("a.txt", new string('字', 500)));
        var tab = window.ActiveTab!;
        Assert.False(tab.Editor.WordWrap);
        Assert.Equal(window.Bounds.Width, tab.Editor.Bounds.Width, 0);

        tab.Doc.FilePath = Path.ChangeExtension(tab.Doc.FilePath!, ".md");
        Dispatcher.UIThread.RunJobs();
        Assert.True(tab.Editor.WordWrap);
        Assert.Equal(Page, tab.Editor.Bounds.Width, 0);
        Assert.True(window.ViewModel.IsCenteredLayoutOn);
        Assert.Equal(default, tab.Editor.Padding);
    }
}
