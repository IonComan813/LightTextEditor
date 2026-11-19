using Avalonia.Headless.XUnit;
using Avalonia.Media;
using LightTextEditor.App.Services;

namespace LightTextEditor.App.Tests;

public class FontsTests
{
    [AvaloniaFact]
    public void InstalledFamilies_DropsMissingFonts_AndIsCached()
    {
        // 用本机实际安装、并且能加载的某个字体（不同机器装的字体不同；Type 1 等格式的字体虽然列出但加载不了）
        var installed = FontManager.Current.SystemFonts.Select(f => f.Name).First(name =>
            FontManager.Current.SystemFonts.TryGetGlyphTypeface(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        var list = $"NoSuchFontXyz, {installed}, AnotherMissingFont";
        var family = Fonts.InstalledFamilies(list);
        Assert.Equal(installed, family.Name);
        Assert.Same(family, Fonts.InstalledFamilies(list));
    }

    [AvaloniaFact]
    public void InstalledFamilies_KeepsEmbeddedFonts()
    {
        // 程序自带的字体（WithInterFont）不是系统字体，但可以用
        var family = Fonts.InstalledFamilies("NoSuchFontXyz, fonts:Inter#Inter");
        Assert.Equal("fonts:Inter#Inter", family.ToString());
    }

    [AvaloniaFact]
    public void InstalledFamilies_FallsBackToDefault_WhenNothingIsInstalled() =>
        Assert.Equal(FontFamily.Default, Fonts.InstalledFamilies("NoSuchFontXyz1, NoSuchFontXyz2"));
}
