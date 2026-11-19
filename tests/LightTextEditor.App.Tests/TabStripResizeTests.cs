using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class TabStripResizeTests
{
    private static async Task Settle()
    {
        for (int i = 0; i < 6; i++) { await Task.Delay(40); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task ScrollArrows_OnlyWhenTabsDoNotFit()
    {
        var root = Path.Combine(TestAppBuilder.Root, "resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = Enumerable.Range(1, 5).Select(i =>
        {
            var p = Path.Combine(root, $"文档{i}-有点长的名字.txt");
            File.WriteAllText(p, "x");
            return p;
        }).ToArray();
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(root, "lib"), RestoreSession = false };
        var window = new MainWindow { Width = 700, Height = 300 };
        window.Initialize(files);
        window.Show();
        await Settle();

        var list = window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.Primitives.FATabViewListView>().Single();
        RepeatButton Arrow(string name) => list.GetVisualDescendants().OfType<RepeatButton>().First(b => b.Name == name);
        bool ArrowsShown() => Arrow("ScrollDecreaseButton").IsEffectivelyVisible && Arrow("ScrollIncreaseButton").IsEffectivelyVisible;

        Assert.True(ArrowsShown()); // 窄窗口放不下

        window.Width = 1600; // 例如最大化后放得下
        await Settle();
        Assert.False(Arrow("ScrollDecreaseButton").IsEffectivelyVisible);
        Assert.False(Arrow("ScrollIncreaseButton").IsEffectivelyVisible);
        var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.All(window.AllTabs(), t => Assert.True(t.TabItem.TranslatePoint(default, list)!.Value.X >= 0,
            $"x={t.TabItem.TranslatePoint(default, list)} off={sv.Offset} ext={sv.Extent} vp={sv.Viewport} list={list.Bounds} max={list.MaxWidth} vis={t.TabItem.IsVisible}"));

        window.Width = 700; // 再缩小
        await Settle();
        Assert.True(ArrowsShown());

        window.Width = 1600;
        await Settle();
        Assert.False(ArrowsShown());
    }
}
