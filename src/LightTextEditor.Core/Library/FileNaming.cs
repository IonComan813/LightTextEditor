using System.Text;

namespace LightTextEditor.Core.Library;

/// <summary>为新文件生成文件名：取首行文字，没有则用时间戳。</summary>
public static class FileNaming
{
    public const int MaxBaseNameLength = 50;

    private static readonly char[] InvalidChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>未命名文档在标签页上显示的标题（首行预览）。</summary>
    public static string? FirstLineTitle(string text, int maxLength = 30)
    {
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            // 去掉 Markdown 标题前缀
            line = line.TrimStart('#').Trim();
            if (line.IsEmpty) continue;
            var s = line.ToString();
            return s.Length > maxLength ? s[..maxLength] + "…" : s;
        }
        return null;
    }

    /// <summary>
    /// 根据文本内容建议一个合法的文件名主体（不含扩展名）。
    /// </summary>
    public static string SuggestBaseName(string text, DateTime now)
    {
        var title = FirstLineTitle(text, int.MaxValue);
        var sanitized = title is null ? "" : Sanitize(title);
        return sanitized.Length > 0 ? sanitized : TimestampName(now);
    }

    public static string TimestampName(DateTime now) => now.ToString("yyyy-MM-dd HHmm");

    /// <summary>去掉 Windows 文件名中的非法字符、控制字符、首尾空格和句点，并处理保留名。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool lastWasSpace = false;
        foreach (var c in name)
        {
            char ch = c;
            if (char.IsControl(ch) || Array.IndexOf(InvalidChars, ch) >= 0) ch = ' ';
            if (ch == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else lastWasSpace = false;
            sb.Append(ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.').Trim();
        if (result.Length > MaxBaseNameLength)
        {
            result = result[..MaxBaseNameLength];
            // 避免把代理对截成两半
            if (char.IsHighSurrogate(result[^1])) result = result[..^1];
            result = result.TrimEnd().TrimEnd('.').TrimEnd();
        }

        if (ReservedNames.Contains(result)) result = "_" + result;
        return result;
    }

    /// <summary>在目录中生成不重复的完整路径："名称.md"、"名称 (2).md"……</summary>
    public static string MakeUniquePath(string directory, string baseName, string extension)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;
        var path = Path.Combine(directory, baseName + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{baseName} ({i}){extension}");
        return path;
    }

    /// <summary>重命名时检查用户输入的名称是否可用。返回错误信息，可用时返回 null。</summary>
    public static string? ValidateUserFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "文件名不能为空。";
        if (name.IndexOfAny(InvalidChars) >= 0 || name.Any(char.IsControl))
            return "文件名不能包含下列字符：< > : \" / \\ | ? *";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "文件名不能以句点或空格结尾。";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) return "这是 Windows 的保留名称。";
        return null;
    }
}
