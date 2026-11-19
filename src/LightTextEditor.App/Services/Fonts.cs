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

    /// <summary>把挑好的字体放进应用资源（键：MarkdownFontFamily）。</summary>
    public static void Register(Application app) => app.Resources["MarkdownFontFamily"] = PickCjkFontFamily();
}
