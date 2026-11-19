using Avalonia.Headless.XUnit;
using Avalonia.Media;
using LightTextEditor.App.Services;

namespace LightTextEditor.App.Tests;

public class FontsTests
{
    [AvaloniaFact]
    public void InstalledFamilies_DropsMissingFonts_AndIsCached()
    {
        // Inter 随程序一起加载（WithInterFont），一定存在
        var family = Fonts.InstalledFamilies("NoSuchFontXyz, Inter, AnotherMissingFont");
        Assert.Equal("Inter", family.Name);
        Assert.Same(family, Fonts.InstalledFamilies("NoSuchFontXyz, Inter, AnotherMissingFont"));
    }

    [AvaloniaFact]
    public void InstalledFamilies_FallsBackToDefault_WhenNothingIsInstalled() =>
        Assert.Equal(FontFamily.Default, Fonts.InstalledFamilies("NoSuchFontXyz1, NoSuchFontXyz2"));
}
