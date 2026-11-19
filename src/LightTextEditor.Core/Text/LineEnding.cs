namespace LightTextEditor.Core.Text;

public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

public static class LineEndings
{
    public static string ToNewLineString(this LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Lf => "\n",
        LineEnding.Cr => "\r",
        _ => throw new ArgumentOutOfRangeException(nameof(ending)),
    };

    public static string DisplayName(this LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "Windows (CRLF)",
        LineEnding.Lf => "Unix (LF)",
        LineEnding.Cr => "Macintosh (CR)",
        _ => ending.ToString(),
    };

    /// <summary>
    /// 统计文本中各种换行符出现的次数，返回最多的那种；没有换行符时返回 <paramref name="fallback"/>。
    /// </summary>
    public static LineEnding Detect(string text, LineEnding fallback, out bool isMixed)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        isMixed = kinds > 1;
        if (kinds == 0) return fallback;
        if (crlf >= lf && crlf >= cr) return LineEnding.CrLf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }

    /// <summary>把文本中的所有换行符统一为 <paramref name="ending"/>。</summary>
    public static string Normalize(string text, LineEnding ending)
    {
        var nl = ending.ToNewLineString();
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(nl);
            }
            else if (c == '\n')
            {
                sb.Append(nl);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
