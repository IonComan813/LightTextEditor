using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class MenuHoverTests
{
    private static Point CenterOf(Visual v, Visual root) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root)!.Value;

    [AvaloniaFact]
    public async Task MovingFromSubmenuItemToDisabledItem_ClearsHighlight()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "menu-" + Guid.NewGuid().ToString("N")) };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fileMenu = window.FindControl<MenuItem>("FileMenu")!;
        var change = window.FindControl<MenuItem>("ChangeExtensionMenu")!;
        var move = window.FindControl<MenuItem>("MoveToLibraryMenuItem")!;

        window.MouseDown(CenterOf(fileMenu, window), MouseButton.Left);
        window.MouseUp(CenterOf(fileMenu, window), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(fileMenu.IsSubMenuOpen);
        Assert.False(move.IsEnabled); // 未命名文档不能移动到笔记库

        // 悬停“更改后缀”：高亮并展开子菜单
        var root = (Visual)TopLevel.GetTopLevel(change)!;
        Assert.Same(window, root); // 无头模式下弹出菜单在窗口的覆盖层里
        window.MouseMove(CenterOf(change, window));
        for (int i = 0; i < 10; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.True(change.IsSelected);

        // 移到不可用的“移动到笔记库”：“更改后缀”不再高亮，子菜单收起
        window.MouseMove(CenterOf(move, window));
        for (int i = 0; i < 10; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.False(change.IsSelected);
        Assert.False(change.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void ClickingEmptyMenuBar_ClosesOpenMenu()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "menu-" + Guid.NewGuid().ToString("N")) };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var menu = window.FindControl<Menu>("MainMenu")!;
        var fileMenu = window.FindControl<MenuItem>("FileMenu")!;
        var editMenu = menu.Items.OfType<MenuItem>().ElementAt(1);
        void Click(Point p)
        {
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        Click(CenterOf(fileMenu, window));
        Assert.True(fileMenu.IsSubMenuOpen);
        // 菜单打开时移到另一个菜单：照常切换过去
        window.MouseMove(CenterOf(editMenu, window));
        Dispatcher.UIThread.RunJobs();
        Assert.True(editMenu.IsSubMenuOpen);
        Assert.False(fileMenu.IsSubMenuOpen);

        // 点菜单栏右边的空白处：菜单关闭
        var blank = new Point(window.Bounds.Width - 100, CenterOf(fileMenu, window).Y);
        Click(blank);
        Assert.False(editMenu.IsSubMenuOpen);
        Assert.False(menu.IsOpen);

        // 菜单没打开时点空白处：什么也不发生
        Click(blank);
        Assert.False(menu.IsOpen);
    }
}
