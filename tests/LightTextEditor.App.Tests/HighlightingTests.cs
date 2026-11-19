using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using TextMateSharp.Grammars;

namespace LightTextEditor.App.Tests;

/// <summary>语法高亮：纯文本不安装 TextMate；Markdown 只加载代码块用到的语言。</summary>
public class HighlightingTests
{
    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "hl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    [Theory]
    [InlineData("```python\nx = 1\n```", "source.python")]
    [InlineData("```py\nx = 1\n```", "source.python")]
    [InlineData("~~~ rust\nfn main() {}\n~~~", "source.rust")]
    [InlineData("```{.cpp}\nint x;\n```", "source.cpp")]
    [InlineData("```C#\nvar x = 1;\n```", "source.cs")]
    [InlineData("```javascript title=\"a.js\"\nlet x;\n```", "source.js")]
    [InlineData("文字 <u>下划线</u>", "text.html.basic")]
    public void FenceLanguages_AreMappedToGrammars(string markdown, string scope) =>
        Assert.Contains(scope, SyntaxHighlighting.EmbeddedScopesFor(markdown));

    [Fact]
    public void PlainMarkdown_NeedsNoEmbeddedGrammar() =>
        Assert.Empty(SyntaxHighlighting.EmbeddedScopesFor("# 标题\n\n**粗体** 和 `行内代码`\n\n```\n没有语言的代码块\n```\n"));

    [Fact]
    public void MarkdownRegistry_ServesOnlyUsedLanguages_WithTheirDependencies()
    {
        var options = new MarkdownRegistryOptions(["source.python", "text.html.basic"]);
        Assert.NotNull(options.GetGrammar("text.html.markdown"));
        Assert.NotNull(options.GetGrammar("source.python"));
        Assert.NotNull(options.GetGrammar("source.css"));           // HTML 语法引用的
        Assert.NotNull(options.GetGrammar("source.js"));
        Assert.Null(options.GetGrammar("source.java"));
        Assert.Null(options.GetGrammar("source.rust"));
        Assert.True(options.AllowedScopes.Count < 12, string.Join(", ", options.AllowedScopes));
    }

    [Fact]
    public void EmbeddedCodeBlock_IsStillTokenizedAsItsLanguage()
    {
        var registry = new TextMateSharp.Registry.Registry(new MarkdownRegistryOptions(["source.python"]));
        var grammar = registry.LoadGrammar("text.html.markdown");
        TextMateSharp.Grammars.IStateStack? state = null;
        TextMateSharp.Grammars.ITokenizeLineResult? result = null;
        foreach (var line in new[] { "```python", "def f(x): return x" })
        {
            result = grammar.TokenizeLine(line, state, TimeSpan.MaxValue);
            state = result.RuleStack;
        }
        var scopes = result!.Tokens.SelectMany(t => t.Scopes).ToList();
        Assert.Contains("meta.embedded.block.python", scopes);
        Assert.Contains("storage.type.function.python", scopes);
    }

    [AvaloniaFact]
    public async Task PlainText_HasNoTextMate_UntilExtensionChanges()
    {
        var (window, library) = CreateWindow();
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "x = 1");
        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.False(tab.Highlighting!.IsInstalled);

        await window.ChangeExtensionAsync(tab, ".py");
        Assert.True(tab.Highlighting.IsInstalled);
        Assert.Equal("source.python", tab.Highlighting.Scope);

        await window.ChangeExtensionAsync(tab, ".txt");
        Assert.False(tab.Highlighting.IsInstalled);
    }

    [AvaloniaFact]
    public async Task Markdown_LoadsNewFenceLanguages_AfterTyping()
    {
        var (window, library) = CreateWindow();
        var path = Path.Combine(library, "a.md");
        File.WriteAllText(path, "# 标题\n\n```python\nx = 1\n```\n");
        await window.OpenFilesAsync([path]);
        var hl = window.ActiveTab!.Highlighting!;
        Assert.True(hl.IsInstalled);
        Assert.Contains("source.python", hl.MarkdownScopes!);
        Assert.DoesNotContain("source.rust", hl.MarkdownScopes!);
        int before = hl.MarkdownScopes!.Count;

        var document = window.ActiveTab.Editor.Document;
        document.Insert(document.TextLength, "\n```rust\nfn main() {}\n```\n");
        hl.Rescan(); // 实际中是停止输入 0.8 秒后
        Assert.True(hl.IsInstalled);
        Assert.Contains("source.rust", hl.MarkdownScopes!);
        Assert.Contains("source.python", hl.MarkdownScopes!); // 只增不减
        Assert.True(hl.MarkdownScopes!.Count < before + 4);
    }
}
