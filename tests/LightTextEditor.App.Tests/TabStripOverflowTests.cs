using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class TabStripOverflowTests
{
    [AvaloniaFact]
    public async Task ManyTabsAtStartup_StayLeftOfCaptionButtons_AndShowScrollButtons()
    {
        var root = Path.Combine(TestAppBuilder.Root, "many-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = Enumerable.Range(1, 15).Select(i =>
        {
            var p = Path.Combine(root, $"文档{i:00}-一个比较长的文件名.txt");
            File.WriteAllText(p, "x");
            return p;
        }).ToArray();
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(root, "lib") };
        var window = new MainWindow { Width = 1100, Height = 400 };
        window.Initialize(files);
        window.Show();
        for (int i = 0; i < 10; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }

        var list = window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.Primitives.FATabViewListView>().Single();
        var add = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddButton");
        double captionLeft = window.Bounds.Width - 3 * 46; // 最小化 / 最大化 / 关闭
        var listRight = list.TranslatePoint(new Point(list.Bounds.Width, 0), window)!.Value.X;
        var addRight = add.TranslatePoint(new Point(add.Bounds.Width, 0), window)!.Value.X;
        Assert.True(add.IsEffectivelyVisible);
        Assert.True(listRight <= add.TranslatePoint(default, window)!.Value.X + 0.5);
        Assert.True(addRight <= captionLeft - 8, $"“+”的右边缘 {addRight} 进入了标题栏按钮区域（{captionLeft}）");
        // 标签放不下：左右两端有滚动按钮，选中的（最后一个）标签完整可见
        var dec = list.GetVisualDescendants().OfType<RepeatButton>().First(b => b.Name == "ScrollDecreaseButton");
        var inc = list.GetVisualDescendants().OfType<RepeatButton>().First(b => b.Name == "ScrollIncreaseButton");
        Assert.True(dec.IsEffectivelyVisible);
        Assert.True(inc.IsEffectivelyVisible);
        var last = window.ActiveTab!.TabItem;
        Assert.True(last.TranslatePoint(new Point(last.Bounds.Width, 0), window)!.Value.X <= listRight + 0.5);
    }
}
