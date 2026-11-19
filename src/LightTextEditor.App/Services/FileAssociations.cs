using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LightTextEditor.Core.Library;
using Microsoft.Win32;

namespace LightTextEditor.App.Services;

/// <summary>
/// Windows 文件关联（只写当前用户，不需要管理员权限）：
/// <list type="bullet">
/// <item>把 <see cref="FileTypes.OpenWith"/> 中的后缀加入资源管理器的“打开方式”菜单（OpenWithProgids），不改变默认程序；</item>
/// <item>在“设置 → 应用 → 默认应用”中登记本程序（Capabilities + RegisteredApplications），用户可以在那里把它设为默认程序。</item>
/// </list>
/// 从 Windows 10 起程序不能自己修改默认程序，只能打开设置页让用户选择（<see cref="OpenDefaultAppsSettings"/>）。
/// 安装 / 更新后由 Velopack 的钩子调用 <see cref="Register"/>，卸载前调用 <see cref="Unregister"/>（见 Program.cs）。
/// </summary>
internal static class FileAssociations
{
    public const string AppName = "LightTextEditor";
    public const string ProgId = "LightTextEditor.Document";
    public const string ExeName = "LightTextEditor.exe";

    private const string Classes = @"Software\Classes";
    private const string ProgIdKey = Classes + @"\" + ProgId;
    private const string ApplicationKey = Classes + @"\Applications\" + ExeName;
    private const string AppKey = @"Software\" + AppName;
    private const string CapabilitiesKey = AppKey + @"\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";

    /// <summary>注册表操作（当前用户，HKCU 下的相对路径；测试时替换）。</summary>
    internal interface IRegistry
    {
        /// <summary>写入字符串值；<paramref name="name"/> 为 null 时写默认值。缺少的键自动创建。</summary>
        void SetValue(string key, string? name, string value);

        void DeleteValue(string key, string name);

        void DeleteTree(string key);

        /// <summary>键存在且没有子键和值时删除。</summary>
        void DeleteIfEmpty(string key);
    }

    /// <summary>要写入的全部注册表值（键、值名、数据）。</summary>
    internal static IEnumerable<(string Key, string? Name, string Value)> Entries(string exePath)
    {
        string command = $"\"{exePath}\" \"%1\"";
        string icon = $"\"{exePath}\",0";

        // 文件类型（ProgID）：打开命令和图标
        yield return (ProgIdKey, null, "LightTextEditor 文档");
        yield return (ProgIdKey + @"\DefaultIcon", null, icon);
        yield return (ProgIdKey + @"\shell\open\command", null, command);

        // “打开方式”中显示的程序名和它支持的类型
        yield return (ApplicationKey, "FriendlyAppName", AppName);
        yield return (ApplicationKey + @"\DefaultIcon", null, icon);
        yield return (ApplicationKey + @"\shell\open\command", null, command);

        // “默认应用”设置页中的条目
        yield return (CapabilitiesKey, "ApplicationName", AppName);
        yield return (CapabilitiesKey, "ApplicationDescription", "轻量文本编辑器：纯文本、Markdown 与代码");
        yield return (CapabilitiesKey, "ApplicationIcon", icon);
        yield return (RegisteredApplicationsKey, AppName, CapabilitiesKey);

        foreach (var ext in FileTypes.OpenWith)
        {
            yield return ($@"{Classes}\{ext}\OpenWithProgids", ProgId, "");
            yield return (ApplicationKey + @"\SupportedTypes", ext, "");
            yield return (CapabilitiesKey + @"\FileAssociations", ext, ProgId);
        }
    }

    /// <summary>注册（重复注册会覆盖，例如更新后程序位置或支持的类型变化）。</summary>
    internal static void Register(string exePath, IRegistry registry)
    {
        foreach (var (key, name, value) in Entries(exePath))
            registry.SetValue(key, name, value);
    }

    /// <summary>删除注册的全部内容（各后缀的键本身保留，只删本程序加的值）。</summary>
    internal static void Unregister(IRegistry registry)
    {
        foreach (var ext in FileTypes.OpenWith)
        {
            var key = $@"{Classes}\{ext}\OpenWithProgids";
            registry.DeleteValue(key, ProgId);
            registry.DeleteIfEmpty(key);
            registry.DeleteIfEmpty($@"{Classes}\{ext}");
        }
        registry.DeleteValue(RegisteredApplicationsKey, AppName);
        registry.DeleteTree(CapabilitiesKey);
        registry.DeleteIfEmpty(AppKey);
        registry.DeleteTree(ApplicationKey);
        registry.DeleteIfEmpty(Classes + @"\Applications");
        registry.DeleteTree(ProgIdKey);
    }

    /// <summary>注册并通知资源管理器刷新（Windows；失败时不抛出异常）。</summary>
    public static bool TryRegister(string? exePath = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            Register(exePath, new WindowsRegistry());
            NotifyShell();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>删除注册并通知资源管理器刷新（Windows；失败时不抛出异常）。</summary>
    public static void TryUnregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Unregister(new WindowsRegistry());
            NotifyShell();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // 卸载时尽力而为
        }
    }

    /// <summary>
    /// Windows 默认应用设置页。Windows 11 22H2 起可以直接打开本程序的页面（registeredAppUser 为 RegisteredApplications 中的名字），
    /// 更早的版本忽略这个参数，打开“默认应用”总页。
    /// </summary>
    public const string DefaultAppsSettingsUri = "ms-settings:defaultapps?registeredAppUser=" + AppName;

    /// <summary>打开 Windows 默认应用设置（测试时可替换）。</summary>
    public static Action OpenDefaultAppsSettings { get; set; } = () =>
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo(DefaultAppsSettingsUri) { UseShellExecute = true }); }
        catch (Exception) { /* 打不开时忽略 */ }
    };

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* 只是让资源管理器立即刷新，失败无妨 */ }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsRegistry : IRegistry
    {
        public void SetValue(string key, string? name, string value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            k.SetValue(name ?? "", value, RegistryValueKind.String);
        }

        public void DeleteValue(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void DeleteTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

        public void DeleteIfEmpty(string key)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
            {
                if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            }
            Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
        }
    }
}
