using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using LightTextEditor.Core;

namespace LightTextEditor.App.Services;

/// <summary>
/// 输入法调试日志：设置文件里 <c>"imeDebugLog": true</c> 时（改完重启程序），
/// 把输入法相关的消息写到配置目录下的 ime-debug.log（Windows: %AppData%\LightTextEditor\ime-debug.log）。
/// <list type="bullet">
/// <item>[win32]：窗口收到的输入法消息（Windows），以及当时直接向输入法查询到的组字文字和光标位置；</item>
/// <item>[client]：Avalonia 交给编辑器的组字文字和光标位置，以及编辑器据此算出的光标位置；</item>
/// <item>[draw]：组字文字里的光标实际画在哪里；</item>
/// <item>[input]：最终输入到文档里的文字。</item>
/// </list>
/// 每次启动时清空重写，超过 5 MB 后不再写入。
/// </summary>
internal static class ImeDebugLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static StreamWriter? _writer;
    private static readonly object Gate = new();

    public static bool IsEnabled => _writer is not null;

    public static string FilePath => Path.Combine(AppPaths.ConfigDirectory, "ime-debug.log");

    /// <summary>打开日志文件；在 Windows 上同时记录窗口收到的输入法消息。</summary>
    public static void Start(Window window)
    {
        if (_writer is null)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDirectory);
                _writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception)
            {
                return; // 写不了日志就算了，不影响使用
            }
            Write("log", $"LightTextEditor {typeof(ImeDebugLog).Assembly.GetName().Version}，{RuntimeInformation.OSDescription}，{RuntimeInformation.FrameworkDescription}");
        }
        if (OperatingSystem.IsWindows()) InstallWin32Hook(window);
    }

    /// <summary>关闭日志（测试用）。</summary>
    internal static void Stop()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Write(string source, string message)
    {
        var writer = _writer;
        if (writer is null) return;
        lock (Gate)
        {
            try
            {
                if (writer.BaseStream.Length > MaxBytes) return;
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{source}] {message}");
            }
            catch (Exception)
            {
                // 忽略写日志的错误
            }
        }
    }

    /// <summary>把字符串写成带引号的形式，看得出空格和空字符串；null 写成 null。</summary>
    public static string Quote(string? text) => text is null ? "null" : $"\"{text}\"";

    // ───────────────────────── Windows 输入法消息 ─────────────────────────

    private const uint WM_IME_STARTCOMPOSITION = 0x010D;
    private const uint WM_IME_ENDCOMPOSITION = 0x010E;
    private const uint WM_IME_COMPOSITION = 0x010F;
    private const uint WM_IME_SETCONTEXT = 0x0281;
    private const uint WM_IME_NOTIFY = 0x0282;
    private const uint WM_IME_CHAR = 0x0286;

    private const uint GCS_COMPREADSTR = 0x0001;
    private const uint GCS_COMPSTR = 0x0008;
    private const uint GCS_CURSORPOS = 0x0080;
    private const uint GCS_DELTASTART = 0x0100;
    private const uint GCS_RESULTREADSTR = 0x0200;
    private const uint GCS_RESULTSTR = 0x0800;
    private const uint CS_INSERTCHAR = 0x2000;
    private const uint CS_NOMOVECARET = 0x4000;

    private static void InstallWin32Hook(Window window)
    {
        Win32Properties.AddWndProcHookCallback(window, (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            try
            {
                switch (msg)
                {
                    case WM_IME_STARTCOMPOSITION:
                        Write("win32", "WM_IME_STARTCOMPOSITION");
                        break;
                    case WM_IME_ENDCOMPOSITION:
                        Write("win32", "WM_IME_ENDCOMPOSITION");
                        break;
                    case WM_IME_COMPOSITION:
                        LogComposition(hWnd, wParam, lParam);
                        break;
                    case WM_IME_SETCONTEXT:
                        Write("win32", $"WM_IME_SETCONTEXT active={wParam != 0} lParam=0x{lParam.ToInt64():X}");
                        break;
                    case WM_IME_NOTIFY:
                        Write("win32", $"WM_IME_NOTIFY wParam=0x{wParam.ToInt64():X}");
                        break;
                    case WM_IME_CHAR:
                        Write("win32", $"WM_IME_CHAR {Quote(char.ConvertFromUtf32((int)(wParam.ToInt64() & 0xFFFF)))}");
                        break;
                }
            }
            catch (Exception e)
            {
                Write("win32", "记录消息时出错：" + e.Message);
            }
            return IntPtr.Zero; // 只记录，照常交给 Avalonia 处理
        });
    }

    private static void LogComposition(IntPtr hWnd, IntPtr wParam, IntPtr lParam)
    {
        uint flags = (uint)(lParam.ToInt64() & 0xFFFFFFFF);
        var names = new List<string>();
        if ((flags & GCS_COMPREADSTR) != 0) names.Add("COMPREADSTR");
        if ((flags & GCS_COMPSTR) != 0) names.Add("COMPSTR");
        if ((flags & GCS_CURSORPOS) != 0) names.Add("CURSORPOS");
        if ((flags & GCS_DELTASTART) != 0) names.Add("DELTASTART");
        if ((flags & GCS_RESULTREADSTR) != 0) names.Add("RESULTREADSTR");
        if ((flags & GCS_RESULTSTR) != 0) names.Add("RESULTSTR");
        if ((flags & CS_INSERTCHAR) != 0) names.Add("CS_INSERTCHAR");
        if ((flags & CS_NOMOVECARET) != 0) names.Add("CS_NOMOVECARET");

        // 直接向输入法查询（不管 lParam 里有没有对应的标志），看看输入法实际报告的是什么
        string? comp = null, result = null;
        int cursor = int.MinValue, delta = int.MinValue;
        var himc = ImmGetContext(hWnd);
        if (himc != IntPtr.Zero)
        {
            try
            {
                comp = GetString(himc, GCS_COMPSTR);
                result = GetString(himc, GCS_RESULTSTR);
                cursor = ImmGetCompositionStringW(himc, GCS_CURSORPOS, null, 0);
                delta = ImmGetCompositionStringW(himc, GCS_DELTASTART, null, 0);
            }
            finally
            {
                ImmReleaseContext(hWnd, himc);
            }
        }
        Write("win32", $"WM_IME_COMPOSITION wParam=0x{wParam.ToInt64():X} lParam=0x{flags:X} [{string.Join(",", names)}] " +
                       $"查询：COMPSTR={Quote(comp)} CURSORPOS={cursor} DELTASTART={delta} RESULTSTR={Quote(result)}" +
                       (himc == IntPtr.Zero ? "（没有输入法上下文）" : ""));
    }

    private static string? GetString(IntPtr himc, uint index)
    {
        int bytes = ImmGetCompositionStringW(himc, index, null, 0);
        if (bytes <= 0) return bytes == 0 ? "" : null;
        var buffer = new byte[bytes];
        ImmGetCompositionStringW(himc, index, buffer, (uint)bytes);
        return Encoding.Unicode.GetString(buffer);
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(IntPtr hIMC, uint dwIndex, byte[]? lpBuf, uint dwBufLen);
}
