using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>
/// 输入框里的文字整行都看得见（g、j、p 的下半部分不被截掉）。
/// Windows 的界面字体（Segoe UI、微软雅黑）行高约为字号的 1.33 倍，比测试环境的字体高；
/// 这里用几种字体和较大的字号模拟。
/// </summary>
public class TextBoxFitTests
{
    private static readonly (string Family, double Size)[] Fonts =
        [("Inter", 14), ("Inter", 16), ("WenQuanYi Zen Hei", 14), ("WenQuanYi Zen Hei", 16), ("DejaVu Sans", 15)];

    private static MainWindow CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "fit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>文字排版的高度完整落在输入框可见的区域内（返回问题描述，没问题时为 null）。</summary>
    private static string? Check(TextBox box, string name)
    {
        var problems = new List<string>();
        foreach (var (family, size) in Fonts)
        {
            box.FontFamily = new FontFamily(family);
            box.FontSize = size;
            box.Text = "gjpqy 国 Hg";
            Render();
            TopLevel.GetTopLevel(box)!.UpdateLayout();
            var presenter = box.GetVisualDescendants().OfType<TextPresenter>().First();
            var viewport = presenter.FindAncestorOfType<ScrollViewer>() is { } sv ? (Visual)sv : box;
            var layout = presenter.TextLayout;
            var top = presenter.TranslatePoint(default, viewport)!.Value.Y;
            double bottom = top + layout.Height;
            double height = viewport.Bounds.Height;
            if (top < -0.5 || bottom > height + 0.5 || presenter.Bounds.Height < layout.Height - 0.5)
                problems.Add($"{name} {family} {size}: 文字 {top:F1}..{bottom:F1}，可见 0..{height:F1}");
        }
        box.ClearValue(TextBox.FontFamilyProperty);
        box.ClearValue(TextBox.FontSizeProperty);
        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    [AvaloniaFact]
    public void FindAndReplaceBoxes_ShowTheWholeLine()
    {
        var window = CreateWindow();
        window.KeyPress(Avalonia.Input.Key.H, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
        Render();
        var panel = window.FindPanel;
        var problems = new[] { Check(panel.FindTextBox, "查找框"), Check(panel.ReplaceTextBox, "替换框") }.Where(p => p is not null);
        Assert.True(!problems.Any(), string.Join("\n", problems));
    }

    [AvaloniaFact]
    public async Task DialogBoxes_ShowTheWholeLine()
    {
        var window = CreateWindow();
        var problems = new List<string>();

        // 重命名（文件名 + 可编辑的后缀下拉框）
        window.KeyPress(Avalonia.Input.Key.F2, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Render();
        var dialog = FindDialog(window);
        foreach (var (box, i) in dialog.GetVisualDescendants().OfType<TextBox>().Select((b, i) => (b, i)))
            if (Check(box, $"重命名对话框输入框 {i}") is { } p) problems.Add(p);
        dialog.Hide(FAContentDialogResult.None);
        Render();

        // 转到行
        window.KeyPress(Avalonia.Input.Key.G, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
        Render();
        dialog = FindDialog(window);
        foreach (var (box, i) in dialog.GetVisualDescendants().OfType<TextBox>().Select((b, i) => (b, i)))
            if (Check(box, $"转到行输入框 {i}") is { } p) problems.Add(p);
        dialog.Hide(FAContentDialogResult.None);
        Render();

        // 更改后缀（自定义）
        var asking = Dialogs.PromptTextAsync(window, "更改后缀", "新的后缀", ".txt", _ => null);
        Render();
        dialog = FindDialog(window);
        foreach (var (box, i) in dialog.GetVisualDescendants().OfType<TextBox>().Select((b, i) => (b, i)))
            if (Check(box, $"更改后缀输入框 {i}") is { } p) problems.Add(p);
        dialog.Hide(FAContentDialogResult.None);
        await asking;

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static FAContentDialog FindDialog(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single();
    }
}
