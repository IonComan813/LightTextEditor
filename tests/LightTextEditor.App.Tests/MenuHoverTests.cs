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
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "menu-" + Guid.NewGuid().ToString("N")), RestoreSession = false };
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
}
