namespace LightTextEditor.Core;

/// <summary>应用使用的各个目录位置。</summary>
public static class AppPaths
{
    public const string AppName = "LightTextEditor";

    /// <summary>
    /// 设置、会话等配置文件所在目录（Windows: %AppData%\LightTextEditor）。
    /// 可用环境变量 LIGHTTEXTEDITOR_CONFIG_DIR 覆盖（便携模式 / 测试）。
    /// </summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("LIGHTTEXTEDITOR_CONFIG_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>默认笔记库目录（Windows: 文档\LightTextEditor）。</summary>
    public static string DefaultLibraryDirectory
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, AppName);
        }
    }

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>编码记录（用户确认过的文件编码）。</summary>
    public static string EncodingMemoryFile => Path.Combine(ConfigDirectory, "encodings.json");
}
