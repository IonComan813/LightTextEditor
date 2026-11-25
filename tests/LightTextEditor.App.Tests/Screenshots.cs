using Avalonia;
using Avalonia.Controls;
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
    public void FindPanel(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var library = Path.Combine(TestAppBuilder.Root, "shots-find-" + theme);
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Initialize([]);
        window.Show();
        var tab = window.ActiveTab!;
        tab.Doc.Document.Text = string.Join("\n", Enumerable.Range(1, 30).Select(i =>
            $"第 {i} 行：The quick brown fox jumps over the lazy dog. 敏捷的棕色狐狸跳过了懒狗。"));
        tab.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.H, RawInputModifiers.Control, PhysicalKey.H, null);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("狐狸");
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"find-{theme}.png"));
        window.Close();
    }

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

        // 拖到右下方，不挡住正文
        var host = window.FindControl<Border>("NoticeHost")!;
        var grab = host.TranslatePoint(new Avalonia.Point(80, 18), window)!.Value;
        window.MouseDown(grab, Avalonia.Input.MouseButton.Left);
        window.MouseMove(grab + new Avalonia.Point(400, 300));
        window.MouseUp(grab + new Avalonia.Point(400, 300), Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-dragged-{theme}.png"));

        // 试一个错误的编码（Windows-1252），再展开下拉：试过的显示为灰色
        var doc = window.ActiveTab!.Doc;
        doc.Notice!.Actions.Single(x => x.Children is not null).Children!.Single(c => c.Label.StartsWith("Windows-1252")).Execute();
        Dispatcher.UIThread.RunJobs();
        var dropDown = window.GetVisualDescendants().OfType<Avalonia.Controls.DropDownButton>().First();
        dropDown.Flyout!.ShowAt(dropDown);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-choices-{theme}.png"));
        dropDown.Flyout.Hide();

        // 换回 Windows-1251，点 ✓：询问是否转换为 UTF-8
        doc.Notice!.Actions.Single(x => x.Children is not null).Children!.Single(c => c.Label.StartsWith("Windows-1251")).Execute();
        Dispatcher.UIThread.RunJobs();
        doc.Notice!.Actions.Single(x => x.Label == "显示正常").Execute();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-convert-offer-{theme}.png"));

        // 顶部"编码"菜单
        var menu = window.FindControl<Avalonia.Controls.MenuItem>("EncodingMenu")!;
        menu.Open();
        Dispatcher.UIThread.RunJobs();
        var reopen = menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(i => (string?)i.Header == "以其他编码重新打开");
        reopen.Open();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"encoding-menu-{theme}.png"));
        menu.Close();
    }

    [AvaloniaFact]
    public void InstalledDialog()
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var library = Path.Combine(TestAppBuilder.Root, "shots-installed");
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        App.IsFirstRunAfterInstall = true;
        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "installed-dialog.png"));
        window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.FAContentDialog>().Single()
            .Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        App.IsFirstRunAfterInstall = false;
    }
}
