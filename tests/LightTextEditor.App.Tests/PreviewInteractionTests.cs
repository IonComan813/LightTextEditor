using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>预览中的鼠标操作：代码块按钮、任务列表选框、Ctrl+滚轮缩放。</summary>
public class PreviewInteractionTests
{
    private static async Task<(MainWindow Window, DocumentTab Tab)> OpenPreviewAsync(string content)
    {
        var library = Path.Combine(TestAppBuilder.Root, "pi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "note.md");
        File.WriteAllText(path, content);
        await window.OpenFilesAsync([path]);
        window.TogglePreview();
        await Settle();
        return (window, window.ActiveTab!);
    }

    private static async Task Settle(int rounds = 10)
    {
        for (int i = 0; i < rounds; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
    }

    private static Point CenterOf(MainWindow window, Visual v) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;

    private static void Click(MainWindow window, Point p)
    {
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ClickingCodeBlockWrapButtonRepeatedly_StaysInPreview()
    {
        var (window, tab) = await OpenPreviewAsync("# 代码\n\n```csharp\nvar x = 1;\n```\n");
        var toggle = tab.Preview!.GetVisualDescendants().OfType<ToggleButton>()
            .First(b => b.Name == "PART_ToggleTextWrapButton");
        var p = CenterOf(window, toggle);
        for (int i = 0; i < 4; i++) Click(window, p); // 快速连点 = 双击
        Assert.True(tab.IsPreviewing);
        Assert.True(tab.Preview.IsVisible);

        // 双击正文仍然回到编辑
        var text = tab.Preview.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>().First();
        var q = CenterOf(window, text);
        Click(window, q);
        Click(window, q);
        Assert.False(tab.IsPreviewing);
    }

    [AvaloniaFact]
    public async Task ClickingTaskCheckBox_TogglesSource()
    {
        var (window, tab) = await OpenPreviewAsync("# 任务\n\n- [ ] 第一项\n- [x] 第二项\n");
        var boxes = tab.Preview!.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Classes.Contains("TaskList")).ToList();
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0].IsHitTestVisible);

        Click(window, CenterOf(window, boxes[0]));
        Assert.Equal("# 任务\n\n- [x] 第一项\n- [x] 第二项\n", tab.Doc.Document.Text);
        Assert.True(tab.Doc.IsDirty);
        Assert.True(tab.IsPreviewing);

        await Settle(3);
        Click(window, CenterOf(window, boxes[1]));
        Assert.Equal("# 任务\n\n- [x] 第一项\n- [ ] 第二项\n", tab.Doc.Document.Text);
        Assert.False(boxes[1].IsChecked);

        // 回到编辑再进预览，选框状态与源码一致
        window.TogglePreview();
        window.TogglePreview();
        await Settle();
        boxes = tab.Preview.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Classes.Contains("TaskList")).ToList();
        Assert.Equal([true, false], boxes.Select(b => b.IsChecked == true));
    }

    [AvaloniaFact]
    public async Task CtrlWheel_ZoomsPreview()
    {
        var (window, tab) = await OpenPreviewAsync("# 标题\n\n" + string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"第 {i} 段")));
        var preview = tab.Preview!;
        double before = preview.Extent.Height;
        double offset = preview.Offset.Y;

        window.MouseWheel(new Point(450, 300), new Vector(0, 1), RawInputModifiers.Control);
        window.MouseWheel(new Point(450, 300), new Vector(0, 1), RawInputModifiers.Control);
        await Settle(3);
        Assert.Equal(1.2, preview.Zoom, 3);
        Assert.True(preview.Extent.Height > before * 1.1);
        Assert.Equal(offset, preview.Offset.Y); // 没有滚动

        // 回到编辑：编辑器使用同样的缩放
        window.TogglePreview();
        Assert.Equal(App.Settings.EditorFontSize * 1.2, tab.Editor.FontSize, 3);
    }
}
