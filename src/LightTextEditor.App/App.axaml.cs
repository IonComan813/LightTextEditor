using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App;

public partial class App : Application
{
    public static string[] StartupArgs { get; set; } = [];
    public static SingleInstance? SingleInstance { get; set; }

    public static AppSettings Settings { get; internal set; } = new();

    /// <summary>用户确认过的文件编码（下次打开时直接使用）。</summary>
    public static EncodingMemory EncodingMemory { get; internal set; } = new(storePath: null);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        TextEncoding.EnsureCodePagesRegistered();
        CaptionButtonToolTips.Register();
        Settings = JsonStore.LoadSettings();
        EncodingMemory = new EncodingMemory(Core.AppPaths.EncodingMemoryFile);
        ApplyTheme(Settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Initialize(StartupArgs);

            if (SingleInstance is { IsFirstInstance: true } instance)
            {
                instance.ArgumentsReceived += args =>
                    Dispatcher.UIThread.Post(() => window.HandleExternalArguments(args));
                instance.StartListening();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>AppStyles.axaml 中的标记资源，用来在 Application.Styles 里找到它（编译后的 XAML 里没有 StyleInclude 了）。</summary>
    private const string AppStylesMarker = "LteAppStylesMarker";

    /// <summary>已加载 Markdown 资源的 Application（无头测试每个测试一个新的 Application）。</summary>
    private static Application? _markdownResourcesLoadedFor;

    /// <summary>
    /// 第一次需要渲染 Markdown 时（打开预览）才加载预览用的样式、配色、中文字体和行内 HTML 扩展。
    /// 只编辑纯文本 / 代码时，不加载 LiveMarkdown、Markdig、公式（RaTeX）、Mermaid 等程序集，启动更快、占用内存更少。
    /// 库自带的样式插在 AppStyles 之前，与原来在 App.axaml 中的顺序一致。
    /// </summary>
    public static void EnsureMarkdownResources()
    {
        if (Current is not { } app || ReferenceEquals(_markdownResourcesLoadedFor, app)) return;
        _markdownResourcesLoadedFor = app;

        MarkdownHtmlInlines.Register();
        Fonts.Register(app);

        var baseUri = new Uri("avares://LightTextEditor/");
        app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
        {
            Source = new Uri("avares://LightTextEditor/Styles/MarkdownTheme.axaml"),
        });

        int index = app.Styles.Count;
        for (int i = 0; i < app.Styles.Count; i++)
        {
            if (app.Styles[i] is Styles { Resources: { } resources } && resources.ContainsKey(AppStylesMarker))
            {
                index = i;
                break;
            }
        }
        foreach (var source in new[]
                 {
                     "avares://LiveMarkdown.Avalonia/Styles.axaml",
                     "avares://LiveMarkdown.Avalonia.Mermaid/Styles.axaml",
                     "avares://LightTextEditor/Styles/MarkdownStyles.axaml",
                 })
            app.Styles.Insert(index++, new StyleInclude(baseUri) { Source = new Uri(source) });
    }

    public static void SaveSettings()
    {
        try { JsonStore.SaveSettings(Settings); }
        catch (Exception) { /* 设置保存失败不影响使用 */ }
    }
}
