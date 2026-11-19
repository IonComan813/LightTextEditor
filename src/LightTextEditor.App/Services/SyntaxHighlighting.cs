using Avalonia;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;

namespace LightTextEditor.App.Services;

/// <summary>
/// 代码语法高亮：使用 VS Code 的 TextMate 语法和配色（浅色 Light+ / 深色 Dark+），按后缀选择语言。
/// </summary>
internal static class SyntaxHighlighting
{
    private static readonly RegistryOptions Registry = new(ThemeName.LightPlus);
    private static readonly Lazy<IRawTheme> LightTheme = new(() => Registry.LoadTheme(ThemeName.LightPlus));
    private static readonly Lazy<IRawTheme> DarkTheme = new(() => Registry.LoadTheme(ThemeName.DarkPlus));

    /// <summary>这些后缀按纯文本处理，不做高亮。</summary>
    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase) { "", ".txt", ".log" };

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static TextMate.Installation Install(TextEditor editor, string extension)
    {
        var installation = editor.InstallTextMate(Registry);
        ApplyTheme(installation);
        ApplyLanguage(installation, extension);
        return installation;
    }

    /// <summary>后缀对应的 TextMate 语法；没有对应语法时为 null。</summary>
    public static string? ScopeFor(string extension) =>
        PlainText.Contains(extension) ? null : Registry.GetScopeByExtension(extension.ToLowerInvariant());

    /// <summary>后缀对应的语言名（如 "Python"），没有时为 null。</summary>
    public static string? LanguageNameFor(string extension) =>
        PlainText.Contains(extension) ? null : Registry.GetLanguageByExtension(extension.ToLowerInvariant())?.ToString();

    public static void ApplyLanguage(TextMate.Installation installation, string extension)
    {
        var scope = ScopeFor(extension);
        try
        {
            installation.SetGrammar(scope);
        }
        catch (Exception)
        {
            // 语法加载失败时退回纯文本，不影响编辑
            try { installation.SetGrammar(null); } catch (Exception) { }
        }
    }

    public static void ApplyTheme(TextMate.Installation installation) =>
        installation.SetTheme(IsDark ? DarkTheme.Value : LightTheme.Value);
}
