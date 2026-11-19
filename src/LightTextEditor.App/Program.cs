using Avalonia;
using LightTextEditor.App.Services;

namespace LightTextEditor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 已有实例在运行：把要打开的文件交给它，然后退出。
        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            if (instance.SendToFirstInstance(args)) return 0;
            // 发送失败（例如上一个实例正在退出），继续以新实例启动。
        }

        App.StartupArgs = args;
        App.SingleInstance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 设计器也会调用此方法。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
