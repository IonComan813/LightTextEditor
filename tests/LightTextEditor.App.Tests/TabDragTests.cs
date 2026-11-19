using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class TabDragTests
{
    private static Point Center(MainWindow w, DocumentTab t) =>
        t.TabItem.TranslatePoint(new Point(t.TabItem.Bounds.Width / 2, t.TabItem.Bounds.Height / 2), w)!.Value;

    [AvaloniaFact]
    public void DraggingTab_ReordersTabs_AndKeepsThemVisible()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "drag"), RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 500 };
        window.Initialize([]);
        window.Show();
        var a = window.ActiveTab!;
        var b = window.NewDocument();
        var c = window.NewDocument();
        Dispatcher.UIThread.RunJobs();

        var start = Center(window, a);
        var end = Center(window, c) + new Point(10, 0);
        window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i <= 10; i++)
        {
            window.MouseMove(start + (end - start) * (i / 10.0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var tabs = window.AllTabs();
        Assert.Equal(3, tabs.Count);
        Assert.Equal(new[] { b, c, a }, tabs);
        foreach (var t in tabs)
        {
            Assert.True(t.TabItem.IsEffectivelyVisible);
            Assert.Equal(1, t.TabItem.Opacity);
            Assert.True(t.TabItem.Bounds.Width > 10);
        }
        Assert.Same(a, window.ActiveTab);

        // 再拖回最左边
        start = Center(window, a);
        end = Center(window, b) - new Point(30, 0);
        window.MouseDown(start, MouseButton.Left);
        for (int i = 1; i <= 10; i++)
        {
            window.MouseMove(start + (end - start) * (i / 10.0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { a, b, c }, window.AllTabs());
        Assert.All(window.AllTabs(), t => Assert.NotNull(t.Header));
    }

    [AvaloniaFact]
    public void ClickingWithoutMoving_DoesNotReorder()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "drag2"), RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 500 };
        window.Initialize([]);
        window.Show();
        var a = window.ActiveTab!;
        var b = window.NewDocument();
        Dispatcher.UIThread.RunJobs();
        var p = Center(window, a);
        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(p + new Point(3, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(p + new Point(3, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { a, b }, window.AllTabs());
        Assert.Same(a, window.ActiveTab); // 单击切换标签
    }

    [AvaloniaFact]
    public async Task DraggedTab_FollowsPointer_AboveOtherTabs_ThenSettles()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "drag3"), RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 300 };
        window.Initialize([]);
        window.Show();
        var a = window.ActiveTab!;
        var b = window.NewDocument();
        var c = window.NewDocument();
        Dispatcher.UIThread.RunJobs();

        var start = Center(window, a);
        var mid = Center(window, b) + new Point(5, 0); // 停在 b 上方（已越过 b 的中线）
        window.MouseDown(start, MouseButton.Left);
        for (int i = 1; i <= 8; i++)
        {
            window.MouseMove(start + (mid - start) * (i / 8.0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(new[] { b, a, c }, window.AllTabs());
        // 被拖动的标签中心跟着鼠标，盖在其他标签上面
        Assert.InRange(Center(window, a).X, mid.X - 2, mid.X + 2);
        Assert.True(a.TabItem.ZIndex > b.TabItem.ZIndex);

        await Task.Delay(80);
        Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR") is { Length: > 0 } dir)
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "tab-drag.png"));

        window.MouseUp(mid, MouseButton.Left);
        for (int i = 0; i < 8; i++) { await Task.Delay(40); Dispatcher.UIThread.RunJobs(); }
        foreach (var t in window.AllTabs())
        {
            Assert.Equal(0, (t.TabItem.RenderTransform as Avalonia.Media.TranslateTransform)?.X ?? 0, 3);
            Assert.Equal(0, t.TabItem.ZIndex);
        }
        Assert.Equal(new[] { b, a, c }, window.AllTabs());
    }
}
