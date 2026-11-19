namespace LightTextEditor.Core.Library;

/// <summary>常用文件类型（后缀 → 显示名称）。</summary>
public static class FileTypes
{
    public sealed record FileType(string Extension, string Name)
    {
        public string Label => $"{Name} ({Extension})";
    }

    /// <summary>保存对话框和"更改文件类型"菜单里列出的常用后缀。</summary>
    public static readonly IReadOnlyList<FileType> Common =
    [
        new(".txt", "纯文本"),
        new(".md", "Markdown"),
        new(".json", "JSON"),
        new(".xml", "XML"),
        new(".yaml", "YAML"),
        new(".ini", "INI 配置"),
        new(".csv", "CSV"),
        new(".log", "日志"),
        new(".html", "HTML"),
        new(".css", "CSS"),
        new(".js", "JavaScript"),
        new(".ts", "TypeScript"),
        new(".py", "Python"),
        new(".cs", "C#"),
        new(".java", "Java"),
        new(".c", "C"),
        new(".cpp", "C++"),
        new(".h", "C/C++ 头文件"),
        new(".go", "Go"),
        new(".rs", "Rust"),
        new(".sql", "SQL"),
        new(".sh", "Shell 脚本"),
        new(".ps1", "PowerShell"),
        new(".bat", "批处理"),
    ];

    /// <summary>
    /// 安装后加入资源管理器“打开方式”菜单的后缀：纯文本、Markdown、配置 / 数据文件和常见代码文件。
    /// 只是出现在“打开方式”里，不会改变这些文件的默认程序（默认程序由用户在 Windows 设置中选择）。
    /// 不包含双击就会执行或导入的类型中危险性较高的 .reg、.vbs 等。
    /// </summary>
    public static readonly IReadOnlyList<string> OpenWith =
    [
        // 文本、文档
        ".txt", ".text", ".md", ".markdown", ".log", ".rst", ".adoc", ".tex", ".srt",
        // 配置、数据
        ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".config", ".properties", ".env", ".editorconfig", ".gitignore", ".gitattributes", ".diff", ".patch",
        // 网页
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".vue", ".svelte",
        // 脚本
        ".py", ".pyw", ".rb", ".php", ".pl", ".lua", ".r", ".sh", ".bash", ".zsh", ".fish",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
        // .NET / JVM
        ".cs", ".csx", ".fs", ".fsx", ".vb", ".xaml", ".axaml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets",
        ".sln", ".slnx", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        // C 系、其他编译型语言
        ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".m", ".mm", ".swift", ".go", ".rs",
        ".dart", ".zig", ".asm",
        // 构建、数据库、接口
        ".sql", ".cmake", ".mk", ".dockerfile", ".proto", ".graphql", ".gql",
    ];

    /// <summary>状态栏显示的名称，如 "纯文本"、"Python"、".abc 文件"。</summary>
    public static string DisplayName(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "无后缀";
        var known = Common.FirstOrDefault(t => t.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
        return known?.Name ?? extension.ToLowerInvariant() + " 文件";
    }

    /// <summary>把用户输入的后缀规范化："md" → ".md"，"*.md" → ".md"，空白 → ""。</summary>
    public static string NormalizeExtension(string? input)
    {
        var s = (input ?? "").Trim().TrimStart('*').Trim();
        if (s.Length == 0 || s == ".") return "";
        return s.StartsWith('.') ? s : "." + s;
    }
}
