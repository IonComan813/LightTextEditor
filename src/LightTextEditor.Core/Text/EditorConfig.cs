using System.Text;
using System.Text.RegularExpressions;

namespace LightTextEditor.Core.Text;

/// <summary>
/// 读取 <c>.editorconfig</c> 中适用于某个文件的属性（目前只用到 <c>charset</c>）。
/// 从文件所在目录逐级向上查找，遇到 <c>root = true</c> 停止；离文件越近、在文件中越靠后的设置优先。
/// 规范见 https://spec.editorconfig.org/ 。
/// </summary>
public static class EditorConfig
{
    public const string FileName = ".editorconfig";

    /// <summary>
    /// 返回 <c>.editorconfig</c> 为该文件指定的 <c>charset</c>（小写，如 "utf-8"、"utf-8-bom"、"latin1"、
    /// "utf-16le"、"utf-16be"）；未指定、指定为 "unset" 或读取失败时返回 null。
    /// </summary>
    public static string? GetCharset(string filePath)
    {
        var value = GetProperty(filePath, "charset");
        return value is null or "unset" ? null : value;
    }

    /// <summary>
    /// 把 <c>charset</c> 转换为保存时使用的编码；不认识或未指定时返回 null。
    /// </summary>
    public static TextEncoding? GetEncoding(string filePath) => GetCharset(filePath) is { } charset ? CharsetToEncoding(charset) : null;

    public static TextEncoding? CharsetToEncoding(string charset) => charset switch
    {
        "utf-8" => TextEncoding.Utf8(withBom: false),
        "utf-8-bom" => TextEncoding.Utf8(withBom: true),
        "latin1" => TextEncoding.FromCodePage(28591),
        "utf-16le" => TextEncoding.FromCodePage(1200),
        "utf-16be" => TextEncoding.FromCodePage(1201),
        _ => null,
    };

    /// <summary>返回适用于该文件的某个属性的值（小写）；没有时返回 null。</summary>
    public static string? GetProperty(string filePath, string property)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        property = property.ToLowerInvariant();
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var configPath = Path.Combine(dir, FileName);
            var parsed = TryParse(configPath);
            if (parsed is not null)
            {
                // 同一文件中靠后的节优先
                for (int i = parsed.Sections.Count - 1; i >= 0; i--)
                {
                    var section = parsed.Sections[i];
                    if (section.Properties.TryGetValue(property, out var value) && Matches(section.Glob, dir, fullPath))
                        return value;
                }
                if (parsed.IsRoot) break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ───────────────────────── 解析 ─────────────────────────

    internal sealed record Section(string Glob, Dictionary<string, string> Properties);

    internal sealed record ParsedFile(bool IsRoot, List<Section> Sections);

    private static ParsedFile? TryParse(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static ParsedFile Parse(string content)
    {
        bool isRoot = false;
        var sections = new List<Section>();
        Section? current = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line[0] == '[')
            {
                int end = line.LastIndexOf(']');
                if (end <= 0) continue;
                current = new Section(line[1..end], new Dictionary<string, string>(StringComparer.Ordinal));
                sections.Add(current);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim().ToLowerInvariant();

            if (current is null)
            {
                if (key == "root") isRoot = value == "true";
            }
            else
            {
                current.Properties[key] = value;
            }
        }
        return new ParsedFile(isRoot, sections);
    }

    // ───────────────────────── 通配符匹配 ─────────────────────────

    /// <summary>判断节名 <paramref name="glob"/>（相对于 <paramref name="configDirectory"/>）是否匹配文件。</summary>
    internal static bool Matches(string glob, string configDirectory, string fullPath)
    {
        var relative = Path.GetRelativePath(configDirectory, fullPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal)) return false;

        // 不含 "/" 的模式匹配任意层级下的文件名；含 "/" 的模式相对于 .editorconfig 所在目录。
        if (glob.StartsWith('/')) glob = glob[1..];
        else if (!glob.Contains('/')) glob = "**/" + glob;

        try
        {
            var regex = new Regex("^" + GlobToRegex(glob) + "$",
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None,
                TimeSpan.FromMilliseconds(200));
            return regex.IsMatch(relative);
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        int braceDepth = 0;
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '\\' when i + 1 < glob.Length:
                    sb.Append(Regex.Escape(glob[++i].ToString()));
                    break;
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        // "**/" 可以匹配零层目录
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                {
                    int close = glob.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append("\\["); break; }
                    var body = glob[(i + 1)..close];
                    sb.Append('[');
                    if (body.StartsWith('!')) { sb.Append('^'); body = body[1..]; }
                    sb.Append(body.Replace("\\", "\\\\").Replace("[", "\\["));
                    sb.Append(']');
                    i = close;
                    break;
                }
                case '{':
                {
                    int close = FindMatchingBrace(glob, i);
                    if (close < 0) { sb.Append("\\{"); break; }
                    var body = glob[(i + 1)..close];
                    var range = Regex.Match(body, @"^(-?\d+)\.\.(-?\d+)$");
                    if (range.Success)
                    {
                        // {1..3}：数字范围，逐个列出（范围通常很小）
                        int a = int.Parse(range.Groups[1].Value), b = int.Parse(range.Groups[2].Value);
                        if (a > b) (a, b) = (b, a);
                        if (b - a > 10000) { sb.Append("-?\\d+"); }
                        else sb.Append("(?:" + string.Join('|', Enumerable.Range(a, b - a + 1)) + ")");
                        i = close;
                    }
                    else if (!body.Contains(','))
                    {
                        // 没有逗号的 {x} 按字面处理
                        sb.Append(Regex.Escape("{" + body + "}"));
                        i = close;
                    }
                    else
                    {
                        sb.Append("(?:");
                        braceDepth++;
                    }
                    break;
                }
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    break;
                case '}' when braceDepth > 0:
                    sb.Append(')');
                    braceDepth--;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        while (braceDepth-- > 0) sb.Append(')');
        return sb.ToString();
    }

    private static int FindMatchingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
