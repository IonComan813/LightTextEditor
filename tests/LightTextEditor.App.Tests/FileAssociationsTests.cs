using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Library;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>Windows 文件关联（用内存中的假注册表测试）和安装后第一次启动的对话框。</summary>
public class FileAssociationsTests
{
    /// <summary>模拟 HKCU：键 → (值名 → 数据)，默认值的名字是 ""。</summary>
    private sealed class FakeRegistry : FileAssociations.IRegistry
    {
        public readonly Dictionary<string, Dictionary<string, string>> Keys = new(StringComparer.OrdinalIgnoreCase);

        public void SetValue(string key, string? name, string value)
        {
            // 和真的注册表一样，自动创建路径上的父键
            for (int i = key.IndexOf('\\'); i > 0; i = key.IndexOf('\\', i + 1))
                Keys.TryAdd(key[..i], new(StringComparer.OrdinalIgnoreCase));
            if (!Keys.TryGetValue(key, out var values)) Keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            values[name ?? ""] = value;
        }

        public void DeleteValue(string key, string name)
        {
            if (Keys.TryGetValue(key, out var values)) values.Remove(name);
        }

        public void DeleteTree(string key)
        {
            foreach (var k in Keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                                   k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                Keys.Remove(k);
        }

        public void DeleteIfEmpty(string key)
        {
            if (!Keys.TryGetValue(key, out var values) || values.Count > 0) return;
            if (Keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))) return;
            Keys.Remove(key);
        }

        public string? Get(string key, string? name = null) =>
            Keys.TryGetValue(key, out var values) && values.TryGetValue(name ?? "", out var v) ? v : null;
    }

    private const string Exe = @"C:\Users\me\AppData\Local\LightTextEditor\current\LightTextEditor.exe";

    [Fact]
    public void Register_AddsOpenWithEntries_AndDefaultAppsCapabilities()
    {
        var reg = new FakeRegistry();
        FileAssociations.Register(Exe, reg);

        Assert.Equal($"\"{Exe}\" \"%1\"", reg.Get(@"Software\Classes\LightTextEditor.Document\shell\open\command"));
        Assert.Equal($"\"{Exe}\",0", reg.Get(@"Software\Classes\LightTextEditor.Document\DefaultIcon"));
        foreach (var ext in new[] { ".txt", ".md", ".py", ".cs", ".json" })
        {
            Assert.Equal("", reg.Get($@"Software\Classes\{ext}\OpenWithProgids", "LightTextEditor.Document"));
            Assert.Equal("", reg.Get(@"Software\Classes\Applications\LightTextEditor.exe\SupportedTypes", ext));
            Assert.Equal("LightTextEditor.Document", reg.Get(@"Software\LightTextEditor\Capabilities\FileAssociations", ext));
        }
        Assert.Equal("LightTextEditor", reg.Get(@"Software\Classes\Applications\LightTextEditor.exe", "FriendlyAppName"));
        Assert.Equal(@"Software\LightTextEditor\Capabilities", reg.Get(@"Software\RegisteredApplications", "LightTextEditor"));
        // 不改默认程序：各后缀的默认值没有被写
        Assert.Null(reg.Get(@"Software\Classes\.txt"));
    }

    [Fact]
    public void Unregister_RemovesOnlyWhatWasAdded()
    {
        var reg = new FakeRegistry();
        // 安装前就有的：用户自己设置的 .txt 打开方式、另一个程序的登记
        reg.SetValue(@"Software\Classes\.txt\OpenWithProgids", "Notepad++_file", "");
        reg.SetValue(@"Software\RegisteredApplications", "Notepad++", @"Software\Notepad++\Capabilities");
        var before = reg.Keys.ToDictionary(k => k.Key, k => new Dictionary<string, string>(k.Value));

        FileAssociations.Register(Exe, reg);
        FileAssociations.Register(Exe, reg); // 更新后再登记一次也不重复
        FileAssociations.Unregister(reg);

        Assert.Equal(before.Keys.Order(), reg.Keys.Keys.Order());
        foreach (var (key, values) in before) Assert.Equal(values, reg.Keys[key]);
    }

    [Fact]
    public void Register_CoversEveryOpenWithType()
    {
        var reg = new FakeRegistry();
        FileAssociations.Register(Exe, reg);
        Assert.All(FileTypes.OpenWith, ext =>
            Assert.Equal("", reg.Get($@"Software\Classes\{ext}\OpenWithProgids", "LightTextEditor.Document")));
    }

    [AvaloniaTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task FirstRunAfterInstall_ShowsDialog_AndOpensDefaultAppsSettingsWhenChecked(bool check, int expectedOpens)
    {
        int opened = 0;
        var original = FileAssociations.OpenDefaultAppsSettings;
        FileAssociations.OpenDefaultAppsSettings = () => opened++;
        try
        {
            var library = Path.Combine(TestAppBuilder.Root, "fa-" + Guid.NewGuid().ToString("N"));
            App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library };
            App.IsFirstRunAfterInstall = true;
            var window = new MainWindow();
            window.Initialize([]);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(App.IsFirstRunAfterInstall); // 只显示一次

            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single();
            Assert.Equal("安装完成", dialog.Title);
            var box = window.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Single();
            Assert.True(box.IsChecked); // 默认勾选
            box.IsChecked = check;
            dialog.Hide(FAContentDialogResult.None);
            for (int i = 0; i < 20 && opened < expectedOpens; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
            await Task.Delay(100); // 没勾选时也多等一会儿，确认确实没有打开
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expectedOpens, opened);
        }
        finally
        {
            FileAssociations.OpenDefaultAppsSettings = original;
            App.IsFirstRunAfterInstall = false;
        }
    }

    [Fact]
    public void SettingsUri_OpensThisAppsPage() =>
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=LightTextEditor", FileAssociations.DefaultAppsSettingsUri);
}
