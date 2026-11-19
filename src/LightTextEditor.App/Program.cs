using Avalonia;
using LightTextEditor.App.Services;
using Velopack;

namespace LightTextEditor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 安装包（Velopack）在安装、更新、卸载时会带特殊参数启动本程序：必须最先处理，处理完会直接退出。
        // 不是通过安装包运行时（开发、测试）什么也不做。
        var velopack = VelopackApp.Build()
            // 安装后第一次启动：显示“安装完成”对话框（可选打开默认应用设置）
            .OnFirstRun(_ => App.IsFirstRunAfterInstall = true);
        if (OperatingSystem.IsWindows())
        {
            // 加入“打开方式”菜单、在“默认应用”设置中登记；更新后重新登记（新版本可能支持更多类型）
            velopack = velopack
                .OnAfterInstallFastCallback(_ => FileAssociations.TryRegister())
                .OnAfterUpdateFastCallback(_ => FileAssociations.TryRegister())
                .OnBeforeUninstallFastCallback(_ => FileAssociations.TryUnregister());
        }
        velopack.Run();

        // 已有实例在运行：把要打开的文件交给它，然后退出。
        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            if (instance.SendToFirstInstance(args)) return 0;
            // 发送失败（例如上一个实例正在退出），继续以新实例启动。
        }

        App.StartupArgs = args;
        App.SingleInstance = instance;
        var builder = BuildAvaloniaApp();
        if (Environment.GetEnvironmentVariable("LTE_LOG") == "1")
            builder = builder.LogToTextWriter(Console.Out, Avalonia.Logging.LogEventLevel.Warning);
        return builder.StartWithClassicDesktopLifetime(args);
    }

    // 设计器也会调用此方法。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // 调试用：在 Linux 上也让 Avalonia 自己绘制标题栏按钮，以便复现 Windows 上的外观
#pragma warning disable AVALONIA_X11_FORCE_CSD
            .With(new X11PlatformOptions
            {
                ForceDrawnDecorations = Environment.GetEnvironmentVariable("LTE_FORCE_DRAWN_DECORATIONS") == "1",
            })
#pragma warning restore AVALONIA_X11_FORCE_CSD
            .LogToTrace();

}
