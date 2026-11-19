using LightTextEditor.Core.Library;

namespace LightTextEditor.Core.Tests;

public class FileNamingTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 14, 30, 0);

    [Theory]
    [InlineData("# 会议记录\n内容", "会议记录")]
    [InlineData("\n\n   第一行  \n第二行", "第一行")]
    [InlineData("a/b:c*d?\"e<f>|g\\h", "a b c d e f g h")]
    [InlineData("结尾的句点...", "结尾的句点")]
    [InlineData("CON", "_CON")]
    [InlineData("", "2026-11-19 1430")]
    [InlineData("   \n\t\n", "2026-11-19 1430")]
    [InlineData("###", "2026-11-19 1430")]
    public void SuggestBaseName(string text, string expected)
    {
        Assert.Equal(expected, FileNaming.SuggestBaseName(text, Now));
    }

    [Fact]
    public void SuggestBaseName_TruncatesLongTitles()
    {
        var name = FileNaming.SuggestBaseName(new string('长', 200), Now);
        Assert.Equal(FileNaming.MaxBaseNameLength, name.Length);
    }

    [Fact]
    public void SuggestBaseName_DoesNotSplitSurrogatePairs()
    {
        var text = new string('a', FileNaming.MaxBaseNameLength - 1) + "😀😀";
        var name = FileNaming.SuggestBaseName(text, Now);
        Assert.False(char.IsHighSurrogate(name[^1]));
    }

    [Fact]
    public void MakeUniquePath_AppendsCounter()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("笔记.md"), "");
        File.WriteAllText(dir.File("笔记 (2).md"), "");
        Assert.Equal(dir.File("笔记 (3).md"), FileNaming.MakeUniquePath(dir.Path, "笔记", ".md"));
        Assert.Equal(dir.File("新的.md"), FileNaming.MakeUniquePath(dir.Path, "新的", "md"));
    }

    [Theory]
    [InlineData("ok.md", true)]
    [InlineData("", false)]
    [InlineData("a?b.md", false)]
    [InlineData("trailing.", false)]
    [InlineData("NUL.txt", false)]
    public void ValidateUserFileName(string name, bool valid)
    {
        Assert.Equal(valid, FileNaming.ValidateUserFileName(name) is null);
    }

    [Fact]
    public void FirstLineTitle_TruncatesWithEllipsis()
    {
        Assert.Equal("abc…", FileNaming.FirstLineTitle("abcdef", 3));
        Assert.Null(FileNaming.FirstLineTitle("\n  \n"));
    }
}
