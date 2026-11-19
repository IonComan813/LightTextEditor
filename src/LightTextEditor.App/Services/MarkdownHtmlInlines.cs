using System.Text.RegularExpressions;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LightTextEditor.App.Services;

/// <summary>
/// 预览中支持常用的行内 HTML 标签（LiveMarkdown 本身不渲染 HTML）：
/// 解析完成后，把成对的 &lt;u&gt;、&lt;sub&gt;、&lt;sup&gt;、&lt;mark&gt; 等标签换成对应的 Markdig 强调语法节点
/// （与 ++下划线++、~下标~、^上标^、==高亮== 相同），&lt;br&gt; 换成换行。
/// </summary>
internal static partial class MarkdownHtmlInlines
{
    private static readonly Dictionary<string, (char Char, int Count)> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["u"] = ('+', 2), ["ins"] = ('+', 2),
        ["sub"] = ('~', 1), ["sup"] = ('^', 1),
        ["mark"] = ('=', 2),
        ["b"] = ('*', 2), ["strong"] = ('*', 2),
        ["i"] = ('*', 1), ["em"] = ('*', 1),
        ["s"] = ('~', 2), ["del"] = ('~', 2), ["strike"] = ('~', 2),
    };

    private static bool _registered;

    /// <summary>
    /// 在第一次渲染之前调用（LiveMarkdown 的解析管线是延迟创建的）：
    /// 行内 HTML 标签、LaTeX 公式（$…$ / $$…$$）、Mermaid 图表。
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        MarkdownRenderer.ConfigurePipeline += builder =>
        {
            builder.UseMermaid();
            builder.DocumentProcessed += Process;
        };
        MarkdownNode.Edit(nodes => nodes
            .Unregister<AlertBlockNode>()
            .Register<LightTextEditor.App.Views.StyledAlertBlockNode>()
            .Register<MathInlineNode>()
            .Register<MathBlockNode>()
            .Register<MermaidBlockNode>());
    }

    [GeneratedRegex(@"^<\s*(/)?\s*([a-zA-Z]+)\b[^>]*?(/)?\s*>$")]
    private static partial Regex TagRegex();

    private static bool TryParseTag(HtmlInline html, out string name, out bool isClosing, out bool isSelfClosing)
    {
        var m = TagRegex().Match(html.Tag ?? "");
        name = m.Success ? m.Groups[2].Value.ToLowerInvariant() : "";
        isClosing = m.Success && m.Groups[1].Success;
        isSelfClosing = m.Success && m.Groups[3].Success;
        return m.Success;
    }

    internal static void Process(MarkdownDocument document)
    {
        // Descendants 不包含段落最外层的行内容器（LeafBlock.Inline），要单独加上
        var containers = new List<ContainerInline>();
        foreach (var leaf in document.Descendants<LeafBlock>())
        {
            if (leaf.Inline is null) continue;
            containers.Add(leaf.Inline);
            containers.AddRange(leaf.Inline.Descendants<ContainerInline>());
        }
        foreach (var container in containers)
            Transform(container);

        // 上标 / 下标：排版引擎不支持把文字上下偏移，能换成 Unicode 上下标字符的就换（H₂O、x²），否则只缩小字号
        foreach (var leaf in document.Descendants<LeafBlock>())
            if (leaf.Inline is not null) ConvertScripts(leaf.Inline);
    }

    private static void ConvertScripts(ContainerInline container)
    {
        var child = container.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            if (child is EmphasisInline emphasis && !TryConvertToUnicodeScript(emphasis))
                ConvertScripts(emphasis);
            else if (child is ContainerInline inner and not EmphasisInline)
                ConvertScripts(inner);
            child = next;
        }
    }

    private const string Plain = "0123456789+-=()abcdefghijklmnoprstuvwxyz";
    private const string Super = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻ";
    // 下标没有对应字符的字母用 '\0' 占位
    private static readonly string Sub = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐ\0\0\0ₑ\0\0ₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥ\0ₓ\0\0";

    private static bool TryConvertToUnicodeScript(EmphasisInline emphasis)
    {
        string? map = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('^', 1) => Super,
            ('~', 1) => Sub,
            _ => null,
        };
        if (map is null) return false;

        var text = new System.Text.StringBuilder();
        foreach (var child in emphasis)
        {
            if (child is not LiteralInline literal) return false;
            foreach (var c in literal.Content.ToString())
            {
                int i = Plain.IndexOf(c == '−' ? '-' : c);
                if (i < 0 || map[i] == '\0') return false;
                text.Append(map[i]);
            }
        }
        if (text.Length == 0) return false;
        ReplaceNode(emphasis, new LiteralInline(text.ToString()) { Span = emphasis.Span, Line = emphasis.Line, Column = emphasis.Column });
        return true;
    }

    /// <summary>
    /// 用 <paramref name="replacement"/> 替换 <paramref name="node"/>。
    /// 不用 Markdig 的 InsertBefore / ReplaceBy：InsertBefore 不设置新节点的 PreviousSibling，之后再删节点会把后面的兄弟节点弄丢。
    /// </summary>
    private static void ReplaceNode(Inline node, Inline replacement)
    {
        node.InsertAfter(replacement);
        node.Remove();
    }

    private static void Transform(ContainerInline container)
    {
        var child = container.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            if (child is HtmlInline html && TryParseTag(html, out var name, out var closing, out _))
            {
                if (name == "br" && !closing)
                {
                    ReplaceNode(html, new LineBreakInline { IsHard = true, Span = html.Span, Line = html.Line, Column = html.Column });
                }
                else if (!closing && Tags.TryGetValue(name, out var delimiter) && FindClosing(html, name) is { } end)
                {
                    var emphasis = new EmphasisInline
                    {
                        DelimiterChar = delimiter.Char,
                        DelimiterCount = delimiter.Count,
                        Span = new SourceSpan(html.Span.Start, end.Span.End),
                        Line = html.Line,
                        Column = html.Column,
                    };
                    // 先把两个标签之间的节点移进强调节点，再用它替换开始标签，最后删掉结束标签
                    var current = html.NextSibling;
                    while (current is not null && current != end)
                    {
                        var following = current.NextSibling;
                        current.Remove();
                        emphasis.AppendChild(current);
                        current = following;
                    }
                    ReplaceNode(html, emphasis);
                    next = end.NextSibling;
                    end.Remove();
                    Transform(emphasis); // 处理嵌套的标签
                }
            }
            child = next;
        }
    }

    /// <summary>在同一层兄弟节点中找到与开始标签配对的结束标签（考虑同名嵌套）。</summary>
    private static HtmlInline? FindClosing(HtmlInline open, string name)
    {
        int depth = 0;
        for (var node = open.NextSibling; node is not null; node = node.NextSibling)
        {
            if (node is not HtmlInline html || !TryParseTag(html, out var n, out var closing, out var selfClosing) || n != name || selfClosing)
                continue;
            if (!closing) depth++;
            else if (depth == 0) return html;
            else depth--;
        }
        return null;
    }
}
