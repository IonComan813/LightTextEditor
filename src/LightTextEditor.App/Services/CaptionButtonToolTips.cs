using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LightTextEditor.App.Views;

namespace LightTextEditor.App.Services;

/// <summary>
/// 标题栏的最小化 / 最大化 / 关闭按钮位于 Avalonia 12 的"窗口装饰层"，不属于窗口的可视树，
/// 挂在它们上面的 ToolTip 弹不出来。这里监听这些按钮的悬停，由主窗口在按钮下方画提示框。
/// </summary>
internal static class CaptionButtonToolTips
{
    private static IDisposable? _timer;
    private static Button? _pending;

    public static void Register()
    {
        InputElement.PointerEnteredEvent.AddClassHandler<Button>(OnEntered, handledEventsToo: true);
        InputElement.PointerExitedEvent.AddClassHandler<Button>(OnExitedOrPressed, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<Button>(OnExitedOrPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static WindowDecorationsElementRole Role(Button button) => WindowDecorationProperties.GetElementRole(button);

    private static bool IsCaptionButton(Button button) =>
        Role(button) is WindowDecorationsElementRole.MinimizeButton
            or WindowDecorationsElementRole.MaximizeButton
            or WindowDecorationsElementRole.CloseButton;

    private static MainWindow? Window =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    private static void OnEntered(Button button, PointerEventArgs e)
    {
        if (!IsCaptionButton(button)) return;
        Cancel();
        _pending = button;
        _timer = DispatcherTimer.RunOnce(() =>
        {
            if (_pending != button || !button.IsPointerOver) return;
            if (ToolTip.GetTip(button) is string text)
                Window?.ShowCaptionToolTip(Role(button), text);
        }, TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(button)));
    }

    private static void OnExitedOrPressed(Button button, RoutedEventArgs e)
    {
        if (!IsCaptionButton(button)) return;
        Cancel();
        Window?.HideCaptionToolTip();
    }

    private static void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
        _pending = null;
    }
}
