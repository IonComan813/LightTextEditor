using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightTextEditor.App.ViewModels;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using Avalonia.VisualTree;

namespace LightTextEditor.App.Tests;

public class LineNumberToggleTests
{
    [AvaloniaFact]
    public void TogglingLineNumbers_WithSeveralTabs_DoesNotCrash()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "ln") };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();
        window.NewDocument();
        window.NewDocument();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        vm.ShowLineNumbers = false;
        Dispatcher.UIThread.RunJobs();
        vm.ShowLineNumbers = true;
        Dispatcher.UIThread.RunJobs();
        vm.ShowLineNumbers = false;
        Dispatcher.UIThread.RunJobs();
        vm.ShowLineNumbers = true;
        Dispatcher.UIThread.RunJobs();
        // 后台标签页在切到前台时同步
        foreach (var tab in window.AllTabs())
        {
            window.KeyPress(Avalonia.Input.Key.D1 + window.AllTabs().IndexOf(tab), Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(tab, window.ActiveTab);
            Assert.True(tab.Editor.ShowLineNumbers);
        }
    }

    [AvaloniaFact]
    public void TabHeader_SelectedIsBold_OthersHideCloseButton()
    {
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = Path.Combine(TestAppBuilder.Root, "tabs") };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();
        var first = window.ActiveTab!;
        first.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("改了");          // 第一个标签有未保存更改
        var second = window.NewDocument();    // 第二个标签是当前标签
        Dispatcher.UIThread.RunJobs();

        static Avalonia.Controls.TextBlock Title(DocumentTab t) =>
            t.TabItem.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(tb => tb.Classes.Contains("tabTitle"));

        Assert.Equal(Avalonia.Media.FontWeight.Bold, Title(second).FontWeight);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, Title(first).FontWeight);
        Assert.Equal(1, Title(second).Opacity);
        Assert.True(Title(first).Opacity < 0.7);

        // 当前标签：× 可见；非当前但未保存：显示圆点
        Assert.Equal(1, second.CloseButton.Opacity);
        Assert.Equal(1, first.CloseButton.Opacity);
        Assert.Contains("dirty", first.CloseButton.Classes);

        // 非当前、未修改的标签：× 隐藏
        window.KeyPress(Avalonia.Input.Key.D1, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Digit1, "1");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, second.CloseButton.Opacity);
        Assert.False(second.CloseButton.IsHitTestVisible);

    }
}
