using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightTextEditor.App.Services;

namespace LightTextEditor.App.Tests;

/// <summary>窗口在后台 / 最小化时的资源节省。</summary>
public class BackgroundModeTests
{
    private sealed class FakeActions : BackgroundMode.IActions
    {
        public readonly List<string> Log = [];
        public void ReleaseMemory() => Log.Add("release");
        public void SetEfficiencyMode(bool on, bool minimized) => Log.Add(on ? (minimized ? "eco+min" : "eco") : "normal");
        public void SetLowMemoryPriority(bool low) => Log.Add(low ? "lowmem" : "normalmem");
        public void TrimWorkingSet() => Log.Add("trim");
    }

    private static async Task WaitAsync(int ms)
    {
        await Task.Delay(ms);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>等到某个操作发生（最多 5 秒；测试机繁忙时定时器可能晚触发）。</summary>
    private static async Task WaitForAsync(FakeActions actions, string entry)
    {
        for (int i = 0; i < 100 && !actions.Log.Contains(entry); i++) await WaitAsync(50);
    }

    private static (Window, BackgroundMode, FakeActions) Create(int inactiveDelayMs = 150)
    {
        var window = new Window();
        window.Show();
        Dispatcher.UIThread.RunJobs(); // 窗口显示后的激活事件先处理完
        var actions = new FakeActions();
        var mode = new BackgroundMode(window, actions)
        {
            MinimizedDelay = TimeSpan.FromMilliseconds(50),
            InactiveDelay = TimeSpan.FromMilliseconds(inactiveDelayMs),
        };
        return (window, mode, actions);
    }

    [AvaloniaFact]
    public async Task Inactive_EntersEfficiencyMode_AndReleasesMemoryOnceAfterDelay()
    {
        var (_, mode, actions) = Create();
        mode.SetActive(false);
        Assert.Equal(["eco"], actions.Log);
        await WaitForAsync(actions, "release");
        Assert.Equal(["eco", "release"], actions.Log); // 不在最小化：不收缩工作集
        await WaitAsync(300);
        Assert.Single(actions.Log, "release");          // 只做一次

        mode.SetActive(true);
        Assert.Equal("normal", actions.Log[^1]);
    }

    [AvaloniaFact]
    public async Task QuickSwitchBack_DoesNotReleaseMemory()
    {
        // 延迟设得远大于测试中的等待时间，测试机繁忙时也不会误触发
        var (_, mode, actions) = Create(inactiveDelayMs: 5000);
        mode.SetActive(false);
        await WaitAsync(30);
        mode.SetActive(true);
        await WaitAsync(300);
        Assert.Equal(["eco", "normal"], actions.Log);
    }

    [AvaloniaFact]
    public async Task Minimizing_LowersMemoryPriority_ReleasesAndTrims_ThenRestores()
    {
        var (window, mode, actions) = Create();
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();
        Assert.True(mode.IsInBackground);
        Assert.Equal(["lowmem", "eco+min"], actions.Log);
        await WaitForAsync(actions, "trim");
        Assert.Equal(["lowmem", "eco+min", "release", "trim"], actions.Log);

        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs();
        Assert.False(mode.IsInBackground);
        Assert.Equal(["normalmem", "normal"], actions.Log[^2..]);

        // 下次再进入后台时会再回收一次
        actions.Log.Clear();
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();
        await WaitForAsync(actions, "release");
        Assert.Contains("release", actions.Log);
    }

    [AvaloniaFact]
    public async Task MinimizingAfterInactiveRelease_TrimsWithoutReleasingAgain()
    {
        var (_, mode, actions) = Create();
        mode.SetActive(false);
        await WaitForAsync(actions, "release");
        Assert.Equal(["eco", "release"], actions.Log);
        mode.SetMinimized(true);
        await WaitAsync(200);
        Assert.Equal(["eco", "release", "lowmem", "trim", "eco+min"], actions.Log);
    }

    [AvaloniaFact]
    public void MainWindow_HasBackgroundMode() =>
        Assert.NotNull(new LightTextEditor.App.Views.MainWindow().BackgroundMode);
}
