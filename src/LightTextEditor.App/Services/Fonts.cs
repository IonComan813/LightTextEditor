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

    /// <summary>
    /// 字体是否已安装：按名字直接查找（DirectWrite 会匹配各种语言的字体名，如“微软雅黑 UI”与 Microsoft YaHei UI），
    /// 不枚举全部系统字体。找不到时返回 false，不会回退到其他字体。
    /// </summary>
    private static bool IsInstalled(string name)
    {
        // 程序自带或资源中的字体（如 fonts:Inter#Inter、avares://…#名称）不在系统字体里，保留
        if (name.Contains(':') || name.Contains('#')) return true;
        try
        {
            return FontManager.Current.SystemFonts.TryGetGlyphTypeface(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _);
        }
        catch (Exception)
        {
            return true; // 查不了时保守地保留
        }
    }

    private static readonly Dictionary<string, FontFamily> ResolvedLists = new(StringComparer.Ordinal);

    /// <summary>
    /// 把逗号分隔的字体列表（如编辑器字体 "Cascadia Mono, Consolas, Microsoft YaHei UI, …"）中没有安装的去掉。
    /// Avalonia 排版每一段文字时都会按列表顺序逐个查找字体，没安装的每次都查找失败
    /// （例如 Windows 10 没有 Cascadia Mono），去掉它们可以省下这些查找和内存分配。
    /// 一个都没有安装时用默认字体。结果缓存，各个编辑器共用同一个 FontFamily 对象。
    /// </summary>
    public static FontFamily InstalledFamilies(string familyList)
    {
        if (ResolvedLists.TryGetValue(familyList, out var cached)) return cached;
        var names = familyList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(IsInstalled)
            .ToList();
        var family = names.Count == 0 ? FontFamily.Default : new FontFamily(string.Join(", ", names));
        ResolvedLists[familyList] = family;
        return family;
    }

    public static FontFamily PickCjkFontFamily()
    {
        foreach (var name in CjkCandidates)
            if (IsInstalled(name)) return new FontFamily(name);
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
