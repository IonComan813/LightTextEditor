using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace LightTextEditor.App.Services;

/// <summary>
/// 诊断：设置环境变量 LTE_STARTUP_REPORT=文件路径 时，主窗口第一次显示并绘制完成后，
/// 把启动耗时和内存占用写入该文件（制表符分隔），然后退出。用于比较优化前后的启动性能，
/// 例如在 Windows 上：set LTE_STARTUP_REPORT=%TEMP%\lte.tsv 后运行 LightTextEditor.exe。
/// </summary>
internal static class StartupReport
{
    public static void AttachIfRequested(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var path = Environment.GetEnvironmentVariable("LTE_STARTUP_REPORT");
        if (string.IsNullOrEmpty(path)) return;

        window.Opened += (_, _) =>
        {
            // 等第一帧画完，再等界面空闲（后台初始化、着色等）
            Dispatcher.UIThread.Post(() =>
            {
                var shown = DateTime.Now - Process.GetCurrentProcess().StartTime;
                DispatcherTimer.RunOnce(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    using var p = Process.GetCurrentProcess();
                    p.Refresh();
                    var c = CultureInfo.InvariantCulture;
                    File.AppendAllText(path, string.Join('\t',
                        shown.TotalMilliseconds.ToString("0", c),
                        (p.WorkingSet64 / 1048576.0).ToString("0.0", c),
                        (p.PrivateMemorySize64 / 1048576.0).ToString("0.0", c),
                        (GC.GetTotalMemory(false) / 1048576.0).ToString("0.0", c),
                        p.Threads.Count.ToString(c),
                        AppDomain.CurrentDomain.GetAssemblies().Length.ToString(c)) + Environment.NewLine);
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(2));
            }, DispatcherPriority.Background);
        };
    }
}
