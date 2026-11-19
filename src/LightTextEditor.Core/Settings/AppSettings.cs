using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Settings;

public sealed class AppSettings
{
    /// <summary>设置文件的格式版本，用于升级旧设置。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }

    /// <summary>笔记库目录；为空时使用 <see cref="AppPaths.DefaultLibraryDirectory"/>。</summary>
    public string? LibraryPath { get; set; }

    /// <summary>新建文件的默认扩展名。</summary>
    public string DefaultExtension { get; set; } = ".txt";

    /// <summary>新建的 .txt / .md 文件是否写入 UTF-8 BOM（作为编码声明）。代码文件始终不带 BOM。</summary>
    public bool Utf8BomForPlainText { get; set; } = true;

    /// <summary>新建文件使用的换行符。</summary>
    public LineEnding NewFileLineEnding { get; set; } = OperatingSystem.IsWindows() ? LineEnding.CrLf : LineEnding.Lf;

    public bool WordWrap { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool HighlightCurrentLine { get; set; } = true;
    public int TabSize { get; set; } = 4;
    public bool ConvertTabsToSpaces { get; set; } = true;

    public string EditorFontFamily { get; set; } = "Cascadia Mono, Consolas, Microsoft YaHei UI, Noto Sans Mono CJK SC, monospace";
    public double EditorFontSize { get; set; } = 14;

    /// <summary>启动时重新打开上次关闭时打开着的文件。</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>"System" / "Light" / "Dark"。</summary>
    public string Theme { get; set; } = "System";

    public WindowPlacement? Window { get; set; }

    /// <summary>把旧版本的设置升级到当前版本。</summary>
    public void Migrate()
    {
        if (Version < 1)
        {
            // v1：新建文件默认保存为 .txt（之前是 .md）
            DefaultExtension = ".txt";
        }
        Version = CurrentVersion;
    }

    public string ResolveLibraryPath() =>
        string.IsNullOrWhiteSpace(LibraryPath) ? AppPaths.DefaultLibraryDirectory : LibraryPath!;

    /// <summary>根据扩展名决定新文件是否写 BOM。</summary>
    public bool ShouldWriteBom(string extension) =>
        Utf8BomForPlainText && IsPlainTextExtension(extension);

    public static bool IsPlainTextExtension(string extension) =>
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}
