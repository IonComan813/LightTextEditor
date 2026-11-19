using Avalonia;
using Avalonia.Media;

namespace LightTextEditor.App.Services;

/// <summary>
/// 挑选界面用的中文字体。
/// Avalonia 只有在“按名字找到的单个字体”缺少斜体 / 粗体字形时才会合成（倾斜 / 加粗）；
/// 逗号分隔的字体列表或系统回退找到的字体不会合成，中文斜体、粗斜体就显示成常规字体。
/// 所以这里从候选中挑出第一个已安装的中文字体（它们都自带英文字形），只用这一个。
/// </summary>
internal static class Fonts
{
    private static readonly string[] CjkCandidates =
    [
        "Microsoft YaHei UI", "Microsoft YaHei", // Windows
        "PingFang SC", "Hiragino Sans GB",       // macOS
        "Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Micro Hei", // Linux
    ];

    public static FontFamily PickCjkFontFamily()
    {
        try
        {
            var installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var name in CjkCandidates)
                if (installed.Contains(name)) return new FontFamily(name);
        }
        catch (Exception)
        {
            // 字体枚举失败时用默认字体
        }
        return FontFamily.Default;
    }

    /// <summary>把挑好的字体放进应用资源（键：MarkdownFontFamily），以及配套的下划线（键：MarkdownUnderline）。</summary>
    public static void Register(Application app)
    {
        var family = PickCjkFontFamily();
        app.Resources["MarkdownFontFamily"] = family;
        app.Resources["MarkdownUnderline"] = CreateUnderline(family);
    }

    /// <summary>
    /// 连续的下划线。Avalonia 画下划线时会在笔画穿过的地方断开（skip-ink），
    /// 中文笔画多、位置低，下划线就成了一段一段的；删除线不做这种处理。
    /// 所以这里用“删除线”，再按字体的度量把它下移到下划线的位置、使用下划线的粗细。
    /// </summary>
    public static TextDecorationCollection CreateUnderline(FontFamily family)
    {
        // 字体读不到度量时的近似值（常见字体的下划线约在删除线下方 0.4em 处，粗约 0.05em）
        double offsetEm = 0.4, thicknessEm = 0.05;
        try
        {
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphTypeface))
            {
                var m = glyphTypeface.Metrics;
                if (m.DesignEmHeight > 0)
                {
                    // 度量值以基线为 0、向下为正
                    if (m.UnderlinePosition > m.StrikethroughPosition)
                        offsetEm = (m.UnderlinePosition - m.StrikethroughPosition) / (double)m.DesignEmHeight;
                    if (m.UnderlineThickness > 0)
                        thicknessEm = m.UnderlineThickness / (double)m.DesignEmHeight;
                }
            }
        }
        catch (Exception)
        {
            // 用近似值
        }

        return
        [
            new TextDecoration
            {
                Location = TextDecorationLocation.Strikethrough,
                StrokeOffset = offsetEm,
                StrokeOffsetUnit = TextDecorationUnit.FontRenderingEmSize,
                StrokeThickness = thicknessEm,
                StrokeThicknessUnit = TextDecorationUnit.FontRenderingEmSize,
            },
        ];
    }
}
