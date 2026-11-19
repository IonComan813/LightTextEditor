using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
}
