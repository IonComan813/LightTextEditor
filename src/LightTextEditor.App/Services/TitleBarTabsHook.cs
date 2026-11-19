using Avalonia;
using Avalonia.Controls;

namespace LightTextEditor.App.Services;

/// <summary>
/// Windows：标签栏在标题栏里。窗口顶部（标题栏高度内）默认按“标题栏”处理（HTCAPTION），
/// 右键标题栏时系统会弹出窗口菜单并抢走焦点，标签的右键菜单就出不来。
/// 这里在消息进入 Avalonia 之前处理：
/// <list type="bullet">
/// <item>WM_NCHITTEST：鼠标在标签上时一律算客户区（HTCLIENT），点击照常交给标签；</item>
/// <item>WM_CONTEXTMENU：在标签上右键时不让系统弹出窗口菜单（标签自己的菜单在右键松开时打开）。</item>
/// </list>
/// 窗口最上方几像素留给调整窗口大小。
/// </summary>
internal static class TitleBarTabsHook
{
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const int HTCLIENT = 1;
    private const double ResizeBorder = 6;

    public static void Install(Window window, Func<Point, bool> isOverTab)
    {
        if (!OperatingSystem.IsWindows()) return;

        Win32Properties.AddWndProcHookCallback(window, (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is not (WM_NCHITTEST or WM_CONTEXTMENU)) return IntPtr.Zero;
            if (msg == WM_CONTEXTMENU && lParam == -1) return IntPtr.Zero; // 键盘（菜单键 / Shift+F10）

            try
            {
                long l = lParam.ToInt64();
                var screen = new PixelPoint((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                var point = window.PointToClient(screen);
                if (window.WindowState != WindowState.Maximized && point.Y < ResizeBorder) return IntPtr.Zero;
                if (!isOverTab(point)) return IntPtr.Zero;
            }
            catch (Exception)
            {
                return IntPtr.Zero; // 出错时交给系统默认处理
            }

            handled = true;
            return msg == WM_NCHITTEST ? HTCLIENT : IntPtr.Zero;
        });
    }
}
