using LightTextEditor.Core.Library;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.Core.Tests;

public class FileTypesTests
{
    [Theory]
    [InlineData("md", ".md")]
    [InlineData(".md", ".md")]
    [InlineData("*.md", ".md")]
    [InlineData("  .py ", ".py")]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData(null, "")]
    public void NormalizeExtension(string? input, string expected) =>
        Assert.Equal(expected, FileTypes.NormalizeExtension(input));

    [Theory]
    [InlineData(".txt", "纯文本")]
    [InlineData(".MD", "Markdown")]
    [InlineData(".abc", ".abc 文件")]
    [InlineData("", "无后缀")]
    public void DisplayName(string ext, string expected) =>
        Assert.Equal(expected, FileTypes.DisplayName(ext));

    [Fact]
    public void OldSettings_AreMigratedToTxtDefault()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "defaultExtension": ".md", "wordWrap": false }""");
        var s = JsonStore.LoadSettings(path);
        Assert.Equal(".txt", s.DefaultExtension);
        Assert.False(s.WordWrap);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);

        // 升级后用户再改成 .md，不会被再次覆盖
        s.DefaultExtension = ".md";
        JsonStore.SaveSettings(s, path);
        Assert.Equal(".md", JsonStore.LoadSettings(path).DefaultExtension);
    }

    [Fact]
    public void FreshSettings_DefaultToTxt() => Assert.Equal(".txt", JsonStore.LoadSettings("/nonexistent/x.json").DefaultExtension);

    [Fact]
    public void OpenWith_IsNormalized_Unique_AndCoversKnownTypes()
    {
        Assert.All(FileTypes.OpenWith, ext =>
        {
            Assert.StartsWith(".", ext);
            Assert.Equal(ext.ToLowerInvariant(), ext);
            Assert.DoesNotContain(" ", ext);
        });
        Assert.Equal(FileTypes.OpenWith.Count, FileTypes.OpenWith.Distinct().Count());
        Assert.Contains(".txt", FileTypes.OpenWith);
        Assert.Contains(".md", FileTypes.OpenWith);
        // 程序认识的类型都能从“打开方式”打开
        Assert.All(FileTypes.Common, t => Assert.Contains(t.Extension, FileTypes.OpenWith));
        // 不包含双击就会导入 / 执行、危险性较高的类型
        Assert.DoesNotContain(".reg", FileTypes.OpenWith);
        Assert.DoesNotContain(".vbs", FileTypes.OpenWith);
        Assert.DoesNotContain(".exe", FileTypes.OpenWith);
    }
}
