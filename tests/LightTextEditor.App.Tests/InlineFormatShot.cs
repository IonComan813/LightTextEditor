using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class InlineFormatShot
{
    public const string Sample = """
        普通文本，**加粗**，*斜体*，***粗斜体***，~~删除线~~，`行内代码`，<u>下划线</u>，H<sub>2</sub>O，x<sup>2</sup>，<mark>高亮</mark>

        Plain, **bold**, *italic*, ***bold italic***, ~~strike~~, `code`, <u>underline</u>, H<sub>2</sub>O, x<sup>2</sup>, <mark>mark</mark>

        Markdig 语法：H~2~O，x^2^，++下划线++，==高亮==

        ##### 五级标题

        ###### 六级标题
        """;

    [AvaloniaFact]
    public async Task Shot()
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var library = Path.Combine(TestAppBuilder.Root, "inline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 420 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "inline.md");
        File.WriteAllText(path, Sample);
        await window.OpenFilesAsync([path]);
        window.TogglePreview();
        for (int i = 0; i < 10; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "inline.png"));
    }
}
