using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class LineEndingTests
{
    [Theory]
    [InlineData("a\r\nb\r\nc", LineEnding.CrLf, false)]
    [InlineData("a\nb\nc", LineEnding.Lf, false)]
    [InlineData("a\rb", LineEnding.Cr, false)]
    [InlineData("a\r\nb\nc\r\n", LineEnding.CrLf, true)]
    [InlineData("a\nb\nc\r\n", LineEnding.Lf, true)]
    [InlineData("no newline", LineEnding.Lf, false)] // 回退值
    public void Detect(string text, LineEnding expected, bool mixed)
    {
        Assert.Equal(expected, LineEndings.Detect(text, LineEnding.Lf, out var isMixed));
        Assert.Equal(mixed, isMixed);
    }

    [Fact]
    public void Normalize_ConvertsAllKinds()
    {
        Assert.Equal("a\r\nb\r\nc\r\n", LineEndings.Normalize("a\nb\rc\r\n", LineEnding.CrLf));
        Assert.Equal("a\nb\nc\n", LineEndings.Normalize("a\r\nb\rc\n", LineEnding.Lf));
    }
}
