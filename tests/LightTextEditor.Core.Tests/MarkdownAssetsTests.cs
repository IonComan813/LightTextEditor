using LightTextEditor.Core.Markdown;

namespace LightTextEditor.Core.Tests;

public class MarkdownAssetsTests
{
    [Theory]
    [InlineData(".md", true)]
    [InlineData(".MARKDOWN", true)]
    [InlineData(".txt", false)]
    public void IsMarkdownExtension(string ext, bool expected) => Assert.Equal(expected, MarkdownAssets.IsMarkdownExtension(ext));

    [Theory]
    [InlineData("a.PNG", true)]
    [InlineData("b.jpeg", true)]
    [InlineData("c.svg", true)]
    [InlineData("d.txt", false)]
    public void IsImageFile(string path, bool expected) => Assert.Equal(expected, MarkdownAssets.IsImageFile(path));

    [Theory]
    [InlineData("读书 笔记", ".PNG", "读书-笔记-20261119-143005.png")]
    [InlineData(null, "png", "image-20261119-143005.png")]
    [InlineData("a:b", ".jpg", "a-b-20261119-143005.jpg")]
    public void MakeImageFileName(string? baseName, string ext, string expected) =>
        Assert.Equal(expected, MarkdownAssets.MakeImageFileName(baseName, ext, new DateTime(2026, 11, 19, 14, 30, 5)));

    [Fact]
    public void NeedsCopy_OnlyForImagesOutsideDocumentFolder()
    {
        using var dir = new TempDir();
        Assert.False(MarkdownAssets.NeedsCopy(dir.Path, Path.Combine(dir.Path, "pics", "a.png")));
        Assert.True(MarkdownAssets.NeedsCopy(Path.Combine(dir.Path, "notes"), Path.Combine(dir.Path, "a.png")));
    }

    [Fact]
    public void RelativeUrl_UsesForwardSlashes()
    {
        using var dir = new TempDir();
        Assert.Equal("assets/a.png", MarkdownAssets.RelativeUrl(dir.Path, Path.Combine(dir.Path, "assets", "a.png")));
    }

    [Theory]
    [InlineData("assets/a.png", "", "![](assets/a.png)")]
    [InlineData("assets/my pic.png", "", "![](<assets/my pic.png>)")]
    [InlineData("assets/a(1).png", "x", "![x](<assets/a(1).png>)")]
    [InlineData("a.png", "[alt]", "![\\[alt\\]](a.png)")]
    public void ImageMarkdown(string url, string alt, string expected) =>
        Assert.Equal(expected, MarkdownAssets.ImageMarkdown(url, alt));
}
