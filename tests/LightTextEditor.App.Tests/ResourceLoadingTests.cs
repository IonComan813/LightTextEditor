using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>Markdown 预览的样式和资源在第一次打开预览时才加载。</summary>
public class ResourceLoadingTests
{
    private static int AppStylesIndex(Application app) =>
        app.Styles.Select((s, i) => (s, i)).Single(x => x.s is Styles { Resources: { } r } && r.ContainsKey("LteAppStylesMarker")).i;

    [AvaloniaFact]
    public async Task MarkdownResources_AreLoadedOnFirstPreview_BeforeAppStyles()
    {
        var app = Application.Current!;
        var library = Path.Combine(TestAppBuilder.Root, "res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();

        var path = Path.Combine(library, "a.md");
        File.WriteAllText(path, "# 标题\n\n**粗体**");
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();

        // 打开 .md 但还没预览：没有 Markdown 样式和资源
        int stylesBefore = app.Styles.Count;
        Assert.False(app.TryGetResource("MarkdownFontFamily", null, out _));
        Assert.False(app.TryGetResource("AlertNoteBrush", Avalonia.Styling.ThemeVariant.Light, out _));

        window.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(stylesBefore + 3, app.Styles.Count);
        Assert.Equal(app.Styles.Count - 1, AppStylesIndex(app)); // 库的样式在 AppStyles 之前，AppStyles 仍然最后
        Assert.True(app.TryGetResource("MarkdownFontFamily", null, out _));
        Assert.True(app.TryGetResource("AlertNoteBrush", Avalonia.Styling.ThemeVariant.Light, out _));

        // 再开一个预览不会重复加载
        window.TogglePreview();
        window.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(stylesBefore + 3, app.Styles.Count);
    }
}
