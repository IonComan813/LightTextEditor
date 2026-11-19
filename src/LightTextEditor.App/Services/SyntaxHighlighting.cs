using System.Collections;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace LightTextEditor.App.Services;

/// <summary>
/// 代码语法高亮：使用 VS Code 的 TextMate 语法和配色（浅色 Light+ / 深色 Dark+），按后缀选择语言。
/// 每个标签页一个 <see cref="EditorHighlighter"/>。
/// </summary>
internal static class SyntaxHighlighting
{
    /// <summary>所有标签页共用的语法 / 配色来源（只读取，不保存编译后的语法）。</summary>
    internal static readonly RegistryOptions Registry = new(ThemeName.LightPlus);
    private static readonly Lazy<IRawTheme> LightTheme = new(() => Registry.LoadTheme(ThemeName.LightPlus));
    private static readonly Lazy<IRawTheme> DarkTheme = new(() => Registry.LoadTheme(ThemeName.DarkPlus));

    public const string MarkdownScope = "text.html.markdown";

    /// <summary>这些后缀按纯文本处理，不做高亮。</summary>
    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase) { "", ".txt", ".log" };

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    internal static IRawTheme CurrentTheme => IsDark ? DarkTheme.Value : LightTheme.Value;

    /// <summary>后缀对应的 TextMate 语法；没有对应语法时为 null。</summary>
    public static string? ScopeFor(string extension) =>
        PlainText.Contains(extension) ? null : Registry.GetScopeByExtension(extension.ToLowerInvariant());

    /// <summary>后缀对应的语言名（如 "Python"），没有时为 null。</summary>
    public static string? LanguageNameFor(string extension) =>
        PlainText.Contains(extension) ? null : Registry.GetLanguageByExtension(extension.ToLowerInvariant())?.ToString();

    // ───────────────────── Markdown 中代码块用到的语法 ─────────────────────

    /// <summary>代码块的语言标记（```python、~~~ js、```{.cpp}）→ 语法。</summary>
    private static readonly Lazy<Dictionary<string, string>> FenceLanguages = new(BuildFenceLanguages);

    private static Dictionary<string, string> BuildFenceLanguages()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in Registry.GetAvailableLanguages())
        {
            if (Registry.GetScopeByLanguageId(language.Id) is not { } scope) continue;
            void Add(string? key)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                map.TryAdd(key.Trim().TrimStart('.').Replace(" ", ""), scope);
            }
            Add(language.Id);
            foreach (var alias in language.Aliases ?? []) Add(alias);
            foreach (var ext in language.Extensions ?? []) Add(ext);
        }
        // 常见写法里语法包没有列出的
        foreach (var (alias, id) in new[] { ("py", "python"), ("js", "javascript"), ("ts", "typescript"), ("sh", "shellscript"),
                     ("bash", "shellscript"), ("zsh", "shellscript"), ("shell", "shellscript"), ("cs", "csharp"), ("c#", "csharp"),
                     ("c++", "cpp"), ("yml", "yaml"), ("ps1", "powershell"), ("rb", "ruby"), ("rs", "rust"), ("kt", "kotlin") })
        {
            if (!map.ContainsKey(alias) && Registry.GetScopeByLanguageId(id) is { } scope) map[alias] = scope;
        }
        return map;
    }

    private static readonly Regex FenceRegex = new(@"^[ \t]{0,3}(?:`{3,}|~{3,})[ \t]*\{?\.?([^\s`{},]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[A-Za-z!/]", RegexOptions.CultureInvariant);

    /// <summary>Markdown 文本中代码块和行内 HTML 需要的语法。</summary>
    internal static HashSet<string> EmbeddedScopesFor(string markdown)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FenceRegex.Matches(markdown))
        {
            if (FenceLanguages.Value.TryGetValue(m.Groups[1].Value, out var scope)) scopes.Add(scope);
        }
        if (HtmlTagRegex.IsMatch(markdown)) scopes.Add("text.html.basic");
        scopes.Remove(MarkdownScope);
        return scopes;
    }

    /// <summary>一个语法直接引用（include）或注入其中（injection，如 Python 中的正则表达式）的其他语法；结果缓存。</summary>
    private static readonly ConcurrentDictionary<string, string[]> DirectDependencies = new(StringComparer.Ordinal);

    /// <summary>这些语法以及它们（递归）引用的语法。</summary>
    internal static HashSet<string> WithDependencies(IEnumerable<string> scopes)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(scopes);
        while (queue.TryDequeue(out var scope))
        {
            if (!result.Add(scope)) continue;
            foreach (var dep in DirectDependencies.GetOrAdd(scope, FindDependencies)) queue.Enqueue(dep);
        }
        return result;
    }

    private static string[] FindDependencies(string scope)
    {
        IRawGrammar? raw;
        try { raw = Registry.GetGrammar(scope); }
        catch (Exception) { return []; }
        if (raw is null) return [];

        // 原始语法是嵌套的字典 / 列表：找出所有不以 # 或 $ 开头的 include（"source.js#expression" 取 "source.js"）
        var found = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<object>();
        stack.Push(raw);
        while (stack.TryPop(out var node))
        {
            if (node is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is "include" && entry.Value is string include && include.Length > 0 && include[0] is not ('#' or '$'))
                    {
                        int hash = include.IndexOf('#');
                        found.Add(hash < 0 ? include : include[..hash]);
                    }
                    else if (entry.Value is not null and not string) stack.Push(entry.Value);
                }
            }
            else if (node is IEnumerable list and not string)
            {
                foreach (var item in list)
                    if (item is not null and not string) stack.Push(item);
            }
        }
        try
        {
            foreach (var injection in Registry.GetInjections(scope) ?? []) found.Add(injection);
        }
        catch (Exception) { /* 没有注入 */ }
        found.Remove(scope);
        return [.. found];
    }
}

/// <summary>
/// Markdown 用的语法来源：只提供文档里实际用到的嵌入语法。
/// TextMate 的 Markdown 语法为代码块引用了约 70 种语言，加载时会把它们全部编译，每个标签页约 30 MB；
/// 这里对用不到的语言返回 null（代码块按普通代码块显示），只占 1~3 MB。
/// </summary>
internal sealed class MarkdownRegistryOptions : IRegistryOptions
{
    private readonly HashSet<string> _allowed;

    public MarkdownRegistryOptions(IEnumerable<string> embeddedScopes)
    {
        EmbeddedScopes = new HashSet<string>(embeddedScopes, StringComparer.Ordinal);
        // 注意不能从 Markdown 语法本身开始找依赖：它直接引用的正是那约 70 种代码块语言
        var roots = new HashSet<string>(EmbeddedScopes);
        foreach (var injection in SyntaxHighlighting.Registry.GetInjections(SyntaxHighlighting.MarkdownScope) ?? []) roots.Add(injection);
        _allowed = SyntaxHighlighting.WithDependencies(roots);
        _allowed.Add(SyntaxHighlighting.MarkdownScope);
    }

    /// <summary>代码块 / 行内 HTML 需要的语法（不含它们的依赖）。</summary>
    public IReadOnlySet<string> EmbeddedScopes { get; }

    /// <summary>实际会提供的全部语法（含依赖）。</summary>
    public IReadOnlySet<string> AllowedScopes => _allowed;

    public IRawGrammar? GetGrammar(string scopeName) =>
        _allowed.Contains(scopeName) ? SyntaxHighlighting.Registry.GetGrammar(scopeName) : null;

    public ICollection<string>? GetInjections(string scopeName) =>
        _allowed.Contains(scopeName) ? SyntaxHighlighting.Registry.GetInjections(scopeName) : null;

    public IRawTheme GetTheme(string scopeName) => SyntaxHighlighting.Registry.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => SyntaxHighlighting.Registry.GetDefaultTheme();
}

/// <summary>
/// 一个编辑器的语法高亮。纯文本不安装 TextMate（不占用内存、不做后台着色）；
/// Markdown 只加载文档里代码块用到的语言，之后写了新语言的代码块时（停止输入片刻后）再补上。
/// </summary>
internal sealed class EditorHighlighter : IDisposable
{
    private readonly TextEditor _editor;
    private TextMate.Installation? _installation;
    private MarkdownRegistryOptions? _markdownOptions;
    private DispatcherTimer? _rescanTimer;
    private bool _disposed;

    /// <summary>当前语法（纯文本为 null）。</summary>
    public string? Scope { get; private set; }

    /// <summary>当前是否安装了 TextMate（测试用）。</summary>
    internal bool IsInstalled => _installation is not null;

    /// <summary>Markdown 时实际提供的语法（含依赖；测试用）。</summary>
    internal IReadOnlySet<string>? MarkdownScopes => _markdownOptions?.AllowedScopes;

    public EditorHighlighter(TextEditor editor, string extension)
    {
        _editor = editor;
        _editor.Document.Changed += OnDocumentChanged;
        SetLanguage(extension);
    }

    /// <summary>按后缀切换语言（改后缀后调用）。</summary>
    public void SetLanguage(string extension)
    {
        var scope = SyntaxHighlighting.ScopeFor(extension);
        if (scope == Scope && (_installation is not null || scope is null)) return;
        Scope = scope;

        if (scope is null)
        {
            Uninstall();
            return;
        }
        if (scope == SyntaxHighlighting.MarkdownScope)
        {
            _markdownOptions = new MarkdownRegistryOptions(SyntaxHighlighting.EmbeddedScopesFor(_editor.Document.Text));
            Install(_markdownOptions);
        }
        else
        {
            _markdownOptions = null;
            Install(SyntaxHighlighting.Registry);
        }
    }

    /// <summary>主题（浅色 / 深色）变化后调用。</summary>
    public void ApplyTheme() => _installation?.SetTheme(SyntaxHighlighting.CurrentTheme);

    private void Install(IRegistryOptions options)
    {
        Uninstall();
        try
        {
            _installation = _editor.InstallTextMate(options);
            _installation.SetTheme(SyntaxHighlighting.CurrentTheme);
            _installation.SetGrammar(Scope);
        }
        catch (Exception)
        {
            // 语法加载失败时退回纯文本，不影响编辑
            Uninstall();
        }
    }

    private void Uninstall()
    {
        _installation?.Dispose();
        _installation = null;
    }

    // ───────────── Markdown：新出现的代码块语言 ─────────────

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_markdownOptions is null) return;
        // 只在可能改变代码块语言 / 行内 HTML 的修改后重新扫描：改动所在行以 ``` / ~~~ 开头或含 <，或一次性改了很多文字（粘贴、重新加载）
        bool relevant = e.InsertionLength > 200 || e.RemovalLength > 200;
        if (!relevant)
        {
            var document = _editor.Document;
            var line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength));
            var text = document.GetText(line.Offset, Math.Min(line.Length, 200)).TrimStart();
            relevant = text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal) || text.Contains('<');
        }
        if (!relevant) return;

        _rescanTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => Rescan());
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    /// <summary>重新扫描代码块语言；有新语言时重新安装（只增不减，避免反复切换）。</summary>
    internal void Rescan()
    {
        _rescanTimer?.Stop();
        if (_disposed || _markdownOptions is null) return;
        var needed = SyntaxHighlighting.EmbeddedScopesFor(_editor.Document.Text);
        if (needed.IsSubsetOf(_markdownOptions.EmbeddedScopes)) return;
        needed.UnionWith(_markdownOptions.EmbeddedScopes);
        _markdownOptions = new MarkdownRegistryOptions(needed);
        Install(_markdownOptions);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rescanTimer?.Stop();
        _editor.Document.Changed -= OnDocumentChanged;
        Uninstall();
    }
}
