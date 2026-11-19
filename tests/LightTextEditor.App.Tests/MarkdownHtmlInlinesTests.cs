using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using LightTextEditor.App.Services;

namespace LightTextEditor.App.Tests;

public class MarkdownHtmlInlinesTests
{
    private static string Node(Inline i) => i switch
    {
        LiteralInline l => $"'{l.Content}'",
        HtmlInline h => $"html'{h.Tag}'",
        LineBreakInline => "BR",
        EmphasisInline e => $"{e.DelimiterChar}{e.DelimiterCount}(" + string.Join(" ", e.Select(Node)) + ")",
        ContainerInline c => string.Join(" ", c.Select(Node)),
        _ => i.GetType().Name,
    };

    private static string Convert(string markdown)
    {
        var doc = Markdown.Parse(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        MarkdownHtmlInlines.Process(doc);
        return string.Join(" / ", doc.Descendants<LeafBlock>().Where(b => b.Inline is not null).Select(b => Node(b.Inline!)));
    }

    [Theory]
    [InlineData("a<u>b</u>c", "'a' +2('b') 'c'")]
    [InlineData("<mark>m</mark>", "=2('m')")]
    [InlineData("<b><i>x</i></b>", "*2(*1('x'))")]
    [InlineData("H<sub>2</sub>O", "'H' '₂' 'O'")]
    [InlineData("x<sup>2</sup> y^10^ H~2~O", "'x' '²' ' y' '¹⁰' ' H' '₂' 'O'")]
    [InlineData("x<sup>Q</sup>", "'x' ^1('Q')")]          // 没有对应上标字符：保留强调节点
    [InlineData("a<br>b", "'a' BR 'b'")]
    [InlineData("a<u>b", "'a' html'<u>' 'b'")]            // 没有配对的结束标签：原样保留
    [InlineData("`<u>code</u>`", "CodeInline")]           // 行内代码里的标签不处理
    public void ConvertsTags(string markdown, string expected) => Assert.Equal(expected, Convert(markdown));
}

