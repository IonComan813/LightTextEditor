using System.Text;
using System.Text.Json;

namespace LightTextEditor.Core.Text;

/// <summary>
/// JSON 的展开（格式化为多行）和压缩（合并为一行）。
/// <para>
/// 直接按字符处理原文，只增删字符串以外的空白：字符串里的转义（如 \u4e2d、\/）、数字的写法都原样保留，
/// 不会像先解析再序列化那样改写内容。处理前先完整校验一遍，不是有效的 JSON 时不改动。
/// </para>
/// </summary>
public static class JsonText
{
    /// <summary>校验时允许的最大嵌套层数。</summary>
    private const int MaxDepth = 1000;

    /// <summary>
    /// 是否为有效的 JSON 对象或数组（顶层是 { } 或 [ ]）。无效时 <paramref name="error"/> 说明原因和位置。
    /// </summary>
    public static bool Validate(string text, out string? error)
    {
        error = null;
        int first = SkipWhitespace(text, 0);
        if (first >= text.Length || text[first] is not ('{' or '['))
        {
            error = "内容不是以 { 或 [ 开头的 JSON 对象或数组。";
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
        try
        {
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException e)
        {
            error = e.LineNumber is { } line
                ? $"第 {line + 1} 行附近有错误。"
                : "格式有错误。";
            return false;
        }
    }

    /// <summary>是否为有效的 JSON 对象或数组。</summary>
    public static bool IsJson(string text) => Validate(text, out _);

    /// <summary>
    /// 展开为多行：每个成员 / 元素一行，按层级缩进，冒号后加一个空格，空的 {} [] 保持在一行
    /// （与 JSON.stringify(value, null, indent) 的样式相同）。调用前应先用 <see cref="Validate"/> 校验。
    /// </summary>
    public static string Format(string text, string indent, string newLine)
    {
        var sb = new StringBuilder(text.Length + text.Length / 2);
        int depth = 0;

        void NewLine()
        {
            sb.Append(newLine);
            for (int d = 0; d < depth; d++) sb.Append(indent);
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    i = CopyString(text, i, sb);
                    break;
                case '{' or '[':
                    sb.Append(c);
                    int next = SkipWhitespace(text, i + 1);
                    if (next < text.Length && text[next] == (c == '{' ? '}' : ']'))
                    {
                        sb.Append(text[next]); // 空对象 / 空数组
                        i = next;
                    }
                    else
                    {
                        depth++;
                        NewLine();
                    }
                    break;
                case '}' or ']':
                    depth--;
                    NewLine();
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c);
                    NewLine();
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                case ' ' or '\t' or '\r' or '\n':
                    break;
                default:
                    sb.Append(c); // 数字、true / false / null
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>压缩为一行：去掉字符串以外的所有空白。调用前应先用 <see cref="Validate"/> 校验。</summary>
    public static string Minify(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') i = CopyString(text, i, sb);
            else if (c is not (' ' or '\t' or '\r' or '\n')) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>原样复制从 <paramref name="start"/>（开头的引号）起的字符串，返回结尾引号的位置。</summary>
    private static int CopyString(string text, int start, StringBuilder sb)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\') i += 2;
            else if (c == '"') break;
            else i++;
        }
        int end = Math.Min(i, text.Length - 1);
        sb.Append(text, start, end - start + 1);
        return end;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n') i++;
        return i;
    }
}
