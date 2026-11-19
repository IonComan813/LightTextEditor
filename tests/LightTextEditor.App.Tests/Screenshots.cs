using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
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
        App.Settings = new AppSettings { LibraryPath = library, RestoreSession = false };

        var saved = Path.Combine(library, "读书笔记.md");
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

        // 不关闭窗口：有未保存的标签页时 Close() 会弹出确认对话框
    }
}
