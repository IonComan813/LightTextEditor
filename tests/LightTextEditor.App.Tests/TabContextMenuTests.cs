using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class TabContextMenuTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RightClickOnTab_OpensContextMenu(bool tabItemHandlesRelease)
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "ctx-" + Guid.NewGuid().ToString("N")) };
        var window = new MainWindow { Width = 1000, Height = 500 };
        window.Initialize([]);
        window.Show();
        var a = window.ActiveTab!;
        var b = window.NewDocument();
        Dispatcher.UIThread.RunJobs();

        var item = a.TabItem;
        // Windows 上标签控件会处理掉右键松开事件，此时不会产生 ContextRequested
        if (tabItemHandlesRelease)
            item.AddHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var p = item.TranslatePoint(new Point(20, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Right);
        window.MouseUp(p, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(window.TabContextMenu);
        Assert.True(window.TabContextMenu!.IsOpen);
        // 菜单真的显示出来了：菜单项有模板、有大小
        var close = window.TabContextMenu.Items.OfType<Avalonia.Controls.MenuItem>().First();
        Assert.Equal("关闭", close.Header);
        Assert.True(close.IsEffectivelyVisible);
        Assert.NotNull(close.Template);
        Assert.True(close.Bounds.Height > 10);
    }
}
