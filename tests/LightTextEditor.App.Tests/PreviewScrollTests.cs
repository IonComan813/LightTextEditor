using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class PreviewScrollTests
{
    [AvaloniaFact]
    public async Task MouseWheel_ScrollsPreview()
    {
        var library = Path.Combine(TestAppBuilder.Root, "scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library };
        var window = new MainWindow { Width = 900, Height = 600 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "long.md");
        File.WriteAllText(path, string.Join("\n\n", Enumerable.Range(1, 120).Select(i => $"段落 {i}")));
        await window.OpenFilesAsync([path]);
        window.TogglePreview();
        for (int i = 0; i < 10; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }

        var preview = window.ActiveTab!.Preview!;
        Assert.True(preview.Extent.Height > preview.Viewport.Height, $"extent {preview.Extent.Height} viewport {preview.Viewport.Height}");
        window.MouseWheel(new Point(450, 300), new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        Assert.True(preview.Offset.Y > 0, $"offset {preview.Offset.Y}");
    }
}
