using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>把示例文档完整渲染成长图，人工检查各种格式（需要 LTE_SCREENSHOT_DIR）。</summary>
public class SampleShot
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task RenderSample(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var library = Path.Combine(TestAppBuilder.Root, "sample-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(library, "assets"));
            using (var s = AssetLoader.Open(new Uri("avares://LightTextEditor/Assets/Samples/markdown-sample.md")))
            using (var f = File.Create(Path.Combine(library, "Markdown 示例.md"))) s.CopyTo(f);
            using (var s = AssetLoader.Open(new Uri("avares://LightTextEditor/Assets/Samples/sample-image.png")))
            using (var f = File.Create(Path.Combine(library, "assets", "示例图片.png"))) s.CopyTo(f);

            App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library };
            var window = new MainWindow { Width = 1000, Height = 5600 };
            window.Initialize([]);
            window.Show();
            await window.OpenFilesAsync([Path.Combine(library, "Markdown 示例.md")]);
            window.TogglePreview();
            for (int i = 0; i < 30; i++) { await Task.Delay(80); Dispatcher.UIThread.RunJobs(); }
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"sample-{theme}.png"));
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}
