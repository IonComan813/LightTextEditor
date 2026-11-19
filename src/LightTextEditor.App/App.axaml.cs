using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        TextEncoding.EnsureCodePagesRegistered();
        CaptionButtonToolTips.Register();
        Settings = JsonStore.LoadSettings();
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

    public static void SaveSettings()
    {
        try { JsonStore.SaveSettings(Settings); }
        catch (Exception) { /* 设置保存失败不影响使用 */ }
    }
}
