using Avalonia;
using Avalonia.Headless;
using LightTextEditor.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace LightTextEditor.App.Tests;

public static class TestAppBuilder
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "lte-uitests-" + Environment.ProcessId);

    public static AppBuilder BuildAvaloniaApp()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("LIGHTTEXTEDITOR_CONFIG_DIR", Path.Combine(Root, "config"));
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
