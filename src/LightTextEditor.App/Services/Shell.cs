using System.Diagnostics;

namespace LightTextEditor.App.Services;

/// <summary>调用系统文件管理器。</summary>
internal static class Shell
{
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Start(folder);
    }

    public static void RevealInFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
        }
        else
        {
            Start(Path.GetDirectoryName(path)!);
        }
    }

    /// <summary>用系统默认程序打开网址或文件。</summary>
    public static void Open(string target) => Start(target);

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Linux 等系统上没有默认关联时忽略
        }
    }
}
