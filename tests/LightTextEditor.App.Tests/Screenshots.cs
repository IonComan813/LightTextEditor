using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>
/// 生成界面截图供人工检查。只有设置了环境变量 LTE_SCREENSHOT_DIR 时才会写文件。
/// </summary>
public class Screenshots
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task MainWindow(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var library = Path.Combine(TestAppBuilder.Root, "shots-" + theme);
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };

        var saved = Path.Combine(library, "读书笔记.txt");
        File.WriteAllText(saved, "# 读书笔记\n\n## 第一章\n\n这是一段已经保存的内容。\nThe quick brown fox jumps over the lazy dog.\n");

        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Initialize([]);
        window.Show();
        await window.OpenFilesAsync([saved]);

        // 第二个标签页：未保存的修改（显示圆点）
        window.NewDocument();
        window.ActiveTab!.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("会议记录\n\n- 讨论 M1 范围\n- 手动保存，Ctrl+S 直接存入笔记库");
        Dispatcher.UIThread.RunJobs();

        window.ActiveTab.Editor.TextArea.Caret.Offset = 2;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"main-{theme}.png"));

        // 切到已保存的标签页
        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"saved-tab-{theme}.png"));

        // 未命名文档第一次保存时的对话框
        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        var saving = window.SaveActiveAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"save-dialog-{theme}.png"));
        window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.FAContentDialog>().Single()
            .Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        await saving;

        // 不关闭窗口：有未保存的标签页时 Close() 会弹出确认对话框
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task EncodingNotice(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var library = Path.Combine(TestAppBuilder.Root, "shots-enc-" + theme);
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };

        Core.Text.TextEncoding.EnsureCodePagesRegistered();
        var path = Path.Combine(library, "旧笔记.txt");
        File.WriteAllBytes(path, System.Text.Encoding.GetEncoding(1251).GetBytes("Привет, мир! Это тест кодировки.\r\nВторая строка."));

        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Initialize([]);
        window.Show();
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-notice-{theme}.png"));

        // 展开"换一个编码"
        var dropDown = window.GetVisualDescendants().OfType<Avalonia.Controls.DropDownButton>().First();
        dropDown.Flyout!.ShowAt(dropDown);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-choices-{theme}.png"));
        dropDown.Flyout.Hide();
    }
}
