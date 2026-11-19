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
