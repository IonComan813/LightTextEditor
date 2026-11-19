using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LightTextEditor.App.Services;

/// <summary>
/// 窗口不在前台时少占系统资源：
/// <list type="bullet">
/// <item>最小化 2 秒后，或在后台（未激活）1 分钟后：做一次压缩式垃圾回收，把空闲的内存还给系统（所有系统）。</item>
/// <item>Windows：未激活或最小化时进入“效率模式”（EcoQoS，调度器优先使用能效核心、较低频率）；
/// 最小化时再降低内存优先级（内存紧张时先换出本程序的页面），回收内存后收缩工作集。回到前台时全部恢复。</item>
/// </list>
/// </summary>
internal sealed class BackgroundMode : IDisposable
{
    /// <summary>具体的系统操作（测试时替换）。</summary>
    internal interface IActions
    {
        /// <summary>压缩式垃圾回收，把空闲内存还给系统。</summary>
        void ReleaseMemory();

        /// <summary>进入 / 退出效率模式（<paramref name="minimized"/>：最小化时更进一步，不需要精确计时）。</summary>
        void SetEfficiencyMode(bool on, bool minimized);

        /// <summary>降低 / 恢复内存优先级（最小化时降低）。</summary>
        void SetLowMemoryPriority(bool low);

        /// <summary>把当前不用的页面移出工作集（最小化并回收内存之后）。</summary>
        void TrimWorkingSet();
    }

    /// <summary>最小化多久后回收内存。</summary>
    public TimeSpan MinimizedDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>在后台（未激活但可见）多久后回收内存。</summary>
    public TimeSpan InactiveDelay { get; init; } = TimeSpan.FromMinutes(1);

    private readonly Window _window;
    private readonly IActions _actions;
    private readonly DispatcherTimer _releaseTimer;
    private bool _active = true;
    private bool _minimized;
    private bool _released;

    public BackgroundMode(Window window, IActions? actions = null)
    {
        _window = window;
        _actions = actions ?? new SystemActions();
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Background);
        _releaseTimer.Tick += (_, _) => OnReleaseTimer();

        _window.Activated += OnActivated;
        _window.Deactivated += OnDeactivated;
        _window.PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>是否处于后台（未激活或最小化）。</summary>
    internal bool IsInBackground => !_active || _minimized;

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) SetMinimized(_window.WindowState == WindowState.Minimized);
    }

    private void OnActivated(object? sender, EventArgs e) => SetActive(true);
    private void OnDeactivated(object? sender, EventArgs e) => SetActive(false);

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Update();
    }

    internal void SetMinimized(bool minimized)
    {
        if (_minimized == minimized) return;
        _minimized = minimized;
        _actions.SetLowMemoryPriority(minimized);
        // 在后台时已经回收过内存（未激活 1 分钟后），之后才最小化：直接收缩工作集
        if (minimized && _released) _actions.TrimWorkingSet();
        Update();
    }

    private void Update()
    {
        _releaseTimer.Stop();
        if (!IsInBackground)
        {
            _released = false;
            _actions.SetEfficiencyMode(false, minimized: false);
            return;
        }
        _actions.SetEfficiencyMode(true, _minimized);
        if (!_released)
        {
            _releaseTimer.Interval = _minimized ? MinimizedDelay : InactiveDelay;
            _releaseTimer.Start();
        }
    }

    private void OnReleaseTimer()
    {
        _releaseTimer.Stop();
        if (!IsInBackground || _released) return;
        _released = true; // 每次进入后台只做一次
        _actions.ReleaseMemory();
        if (_minimized) _actions.TrimWorkingSet(); // 回收时碰过的页面也一起移出
    }

    public void Dispose()
    {
        _releaseTimer.Stop();
        _window.Activated -= OnActivated;
        _window.Deactivated -= OnDeactivated;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _actions.SetEfficiencyMode(false, minimized: false);
    }

    // ───────────────────────── 实际的系统操作 ─────────────────────────

    private sealed class SystemActions : IActions
    {
        public void ReleaseMemory()
        {
            // Aggressive：回收后尽量把空闲的内存段还给系统（.NET 7+）
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        public void SetEfficiencyMode(bool on, bool minimized)
        {
            if (OperatingSystem.IsWindows()) WindowsProcess.SetEfficiencyMode(on, ignoreTimerResolution: on && minimized);
        }

        public void SetLowMemoryPriority(bool low)
        {
            if (OperatingSystem.IsWindows())
                WindowsProcess.SetMemoryPriority(low ? WindowsProcess.MemoryPriorityLow : WindowsProcess.MemoryPriorityNormal);
        }

        public void TrimWorkingSet()
        {
            if (OperatingSystem.IsWindows()) WindowsProcess.TrimWorkingSet();
        }
    }
}

/// <summary>Windows 的进程级资源设置（失败时静默忽略：旧版本 Windows 不支持部分设置）。</summary>
internal static class WindowsProcess
{
    public const uint MemoryPriorityLow = 2;
    public const uint MemoryPriorityNormal = 5;

    private const int ProcessMemoryPriority = 0;
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref MemoryPriorityInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimumSize, nint maximumSize);

    private static IntPtr CurrentProcess => new(-1); // GetCurrentProcess() 的伪句柄

    /// <summary>
    /// 效率模式（EcoQoS）。开启时由系统降低本进程的执行速度以省电；关闭时交还系统决定（不强制高性能）。
    /// <paramref name="ignoreTimerResolution"/>：同时不再为本进程保持高精度计时器（Windows 11+）。
    /// </summary>
    public static void SetEfficiencyMode(bool on, bool ignoreTimerResolution)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return; // 1709 起支持 EXECUTION_SPEED
        var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion };
        if (on)
        {
            state.ControlMask = state.StateMask = PowerThrottlingExecutionSpeed;
            if (ignoreTimerResolution && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                state.ControlMask |= PowerThrottlingIgnoreTimerResolution;
                state.StateMask |= PowerThrottlingIgnoreTimerResolution;
            }
        }
        try { SetProcessInformation(CurrentProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()); }
        catch (Exception) { /* 不支持时忽略 */ }
    }

    /// <summary>内存优先级（Windows 8+）：低优先级的页面在内存紧张时先被换出。</summary>
    public static void SetMemoryPriority(uint priority)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return;
        var info = new MemoryPriorityInformation { MemoryPriority = priority };
        try { SetProcessInformation(CurrentProcess, ProcessMemoryPriority, ref info, (uint)Marshal.SizeOf<MemoryPriorityInformation>()); }
        catch (Exception) { /* 不支持时忽略 */ }
    }

    /// <summary>把当前不用的页面移出工作集（窗口最小化时；需要时系统会自动调回）。</summary>
    public static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(CurrentProcess, -1, -1); }
        catch (Exception) { /* 忽略 */ }
    }
}
