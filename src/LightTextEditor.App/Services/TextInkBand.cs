using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace LightTextEditor.App.Services;

/// <summary>
/// 文字在竖直方向上实际画到的范围（字形的上下边缘），用于只有文字那么高的高亮（查找的匹配项）。
/// <para>
/// 按字体和字号取参考字的墨迹范围：中文等全角字取“国永”，其他取“Hdg”（大写字母顶到下伸部分的底）。
/// 不用匹配文字自己的墨迹：那样“ace”只有 x 高度，各处高亮高矮不一。
/// 每段文字可能由不同的字体画（如英文用 Cascadia Mono、中文回退到微软雅黑），按各自的字体分别取，再合并。
/// 字体里没有参考字时（如没有装中文字体，中文画成方框），改用这段文字实际画出的墨迹。
/// </para>
/// </summary>
internal static class TextInkBand
{
    private const string CjkReference = "国永";
    private const string OtherReference = "Hdg";

    private static readonly Dictionary<(GlyphTypeface, double, bool), (double Above, double Below)?> Cache = new();

    /// <summary>取参考字墨迹范围的方法（测试时可以换掉，模拟字体里没有参考字）。</summary>
    internal static Func<GlyphTypeface, double, bool, (double Above, double Below)?> ReferenceBand { get; set; } = BandOf;

    /// <summary>
    /// 把整行高的矩形 <paramref name="rect"/>（TextView 坐标，已减去滚动）收成其中文字的上下边缘。
    /// [<paramref name="startColumn"/>, <paramref name="endColumn"/>) 是这段文字在显示行中的列。
    /// 取不到字形信息时原样返回。
    /// </summary>
    public static Rect Shrink(TextView textView, VisualLine line, Rect rect, int startColumn, int endColumn)
    {
        double scroll = textView.ScrollOffset.Y;
        var textLine = line.GetTextLineByVisualYPosition(rect.Center.Y + scroll);
        // 各段文字都对齐到这一行的基线画（与 Avalonia 的 TextLine.Draw 相同）
        double baseline = line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) - scroll + textLine.Baseline;

        double above = 0, below = 0;
        bool found = false;
        int column = textLine.FirstTextSourceIndex;
        foreach (var run in textLine.TextRuns)
        {
            int runStart = column;
            column += run.Length;
            if (run is not ShapedTextRun shaped || column <= startColumn || runStart >= endColumn) continue;
            var text = shaped.Text.Span;
            int from = Math.Max(startColumn, runStart) - runStart, to = Math.Min(endColumn, column) - runStart;
            bool cjk = false, other = false;
            for (int i = from; i < to && i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) continue;
                if (IsWide(text[i])) cjk = true; else other = true;
            }
            if (!cjk && !other) other = true; // 只有空白：按普通文字的高度
            var typeface = shaped.GlyphRun.GlyphTypeface;
            double size = shaped.GlyphRun.FontRenderingEmSize;
            foreach (var (wide, wanted) in new[] { (true, cjk), (false, other) })
            {
                if (!wanted || (ReferenceBand(typeface, size, wide) ?? InkOf(shaped.GlyphRun)) is not { } band) continue;
                above = Math.Max(above, band.Above);
                below = Math.Max(below, band.Below);
                found = true;
            }
        }
        if (!found) return rect;
        double top = Math.Max(rect.Top, baseline - above);
        double bottom = Math.Min(rect.Bottom, baseline + below);
        return bottom > top ? new Rect(rect.Left, top, rect.Width, bottom - top) : rect;
    }

    /// <summary>中文、日文、韩文及全角字符（字形基本占满整个字身）。</summary>
    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᇿ'      // 谚文字母
          or >= '⺀' and <= '꓏'      // 中日韩部首、标点、假名、汉字、彝文
          or >= '가' and <= '힯'      // 谚文音节
          or >= '豈' and <= '﫿'      // 兼容汉字
          or >= '︰' and <= '﹏'      // 兼容形式
          or >= '＀' and <= '￯'      // 全角字符
          || char.IsSurrogate(c);             // 扩展区汉字等

    /// <summary>一段已排版文字在基线以上、以下的墨迹高度（像素）。没有墨迹（如全是空格）时为 null。</summary>
    private static (double Above, double Below)? InkOf(GlyphRun run)
    {
        try
        {
            var ink = run.InkBounds;
            double baseline = run.BaselineOrigin.Y;
            return ink.Height > 0 ? (baseline - ink.Top, ink.Bottom - baseline) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>参考字在基线以上、以下的墨迹高度（像素）。字体里没有参考字时为 null。</summary>
    private static (double Above, double Below)? BandOf(GlyphTypeface typeface, double size, bool wide)
    {
        var key = (typeface, size, wide);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        (double, double)? result = null;
        try
        {
            var chars = new List<char>();
            var glyphs = new List<ushort>();
            foreach (var c in wide ? CjkReference : OtherReference)
            {
                if (typeface.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) && glyph != 0)
                {
                    chars.Add(c);
                    glyphs.Add(glyph);
                }
            }
            if (glyphs.Count > 0)
            {
                using var run = new GlyphRun(typeface, size, chars.ToArray(), glyphs);
                var ink = run.InkBounds;
                double baseline = run.BaselineOrigin.Y;
                if (ink.Height > 0) result = (baseline - ink.Top, ink.Bottom - baseline);
            }
        }
        catch (Exception)
        {
            // 取不到就用整行高
        }
        if (Cache.Count > 256) Cache.Clear();
        Cache[key] = result;
        return result;
    }
}
