using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace LightTextEditor.App.Services;

/// <summary>
/// 笔记库以外的文件：在笔记库的“外部文件”文件夹里放一个快捷方式，文件本身仍保存在原位置。
/// Windows 上是 .lnk 快捷方式；其他系统用符号链接。
/// </summary>
internal static class LibraryShortcuts
{
    public const string FolderName = "外部文件";

    public static string FolderPath(string libraryPath) => Path.Combine(libraryPath, FolderName);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>文件是否在笔记库内（包括子文件夹）。</summary>
    public static bool IsInsideLibrary(string libraryPath, string filePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryPath)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(root, PathComparison);
    }

    /// <summary>为笔记库外的文件创建快捷方式（已有指向它的快捷方式时不重复创建）。返回快捷方式路径，失败时为 null。</summary>
    public static string? Ensure(string libraryPath, string targetPath)
    {
        try
        {
            targetPath = Path.GetFullPath(targetPath);
            if (IsInsideLibrary(libraryPath, targetPath)) return null;
            if (Find(libraryPath, targetPath) is { } existing) return existing;

            var folder = FolderPath(libraryPath);
            Directory.CreateDirectory(folder);
            var path = UniqueShortcutPath(folder, Path.GetFileName(targetPath));
            if (OperatingSystem.IsWindows()) CreateWindowsShortcut(path, targetPath);
            else File.CreateSymbolicLink(path, targetPath);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or NotSupportedException)
        {
            return null; // 快捷方式只是方便查找，失败不影响编辑
        }
    }

    /// <summary>删除指向该文件的快捷方式。</summary>
    public static void Remove(string libraryPath, string targetPath)
    {
        try
        {
            if (Find(libraryPath, Path.GetFullPath(targetPath)) is { } shortcut) File.Delete(shortcut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
        }
    }

    /// <summary>找到指向该文件的快捷方式。</summary>
    public static string? Find(string libraryPath, string targetPath)
    {
        var folder = FolderPath(libraryPath);
        if (!Directory.Exists(folder)) return null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (TryResolve(entry, out var target) && string.Equals(target, targetPath, PathComparison))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// 如果 <paramref name="path"/> 是快捷方式 / 符号链接，得到它指向的文件。
    /// 打开文件时必须先解析：直接编辑符号链接时，保存（先写临时文件再替换）会把链接本身换成普通文件。
    /// </summary>
    public static bool TryResolve(string path, out string target)
    {
        target = path;
        try
        {
            if (OperatingSystem.IsWindows() && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var resolved = ReadWindowsShortcut(path);
                if (string.IsNullOrEmpty(resolved)) return false;
                target = Path.GetFullPath(resolved);
                return true;
            }
            var info = new FileInfo(path);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } final)
            {
                target = final.FullName;
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
        }
        return false;
    }

    /// <summary>打开文件前调用：快捷方式 / 符号链接换成真实路径，普通文件原样返回。</summary>
    public static string ResolveForOpening(string path) => TryResolve(path, out var target) ? target : Path.GetFullPath(path);

    private static string UniqueShortcutPath(string folder, string fileName)
    {
        string suffix = OperatingSystem.IsWindows() ? ".lnk" : "";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName + suffix);
        for (int i = 2; File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null; i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}{suffix}");
        return path;
    }

    // ───────────── Windows .lnk（IShellLinkW） ─────────────

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsShortcut(string shortcutPath, string targetPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath)!);
            link.SetDescription(targetPath);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsShortcut(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);
            var buffer = new StringBuilder(32768);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
