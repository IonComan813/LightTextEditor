using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class JsonStoreTests
{
    [Fact]
    public void Settings_RoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var s = new AppSettings { LibraryPath = @"D:\Notes", WordWrap = false, NewFileLineEnding = LineEnding.Lf, EditorFontSize = 16 };
        JsonStore.SaveSettings(s, path);

        var loaded = JsonStore.LoadSettings(path);
        Assert.Equal(@"D:\Notes", loaded.LibraryPath);
        Assert.False(loaded.WordWrap);
        Assert.Equal(LineEnding.Lf, loaded.NewFileLineEnding);
        Assert.Equal(16, loaded.EditorFontSize);
        Assert.Contains("\"lf\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptFile_ReturnsDefaults()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.True(JsonStore.LoadSettings(path).WordWrap);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        Assert.Empty(JsonStore.LoadSession("/nonexistent/dir/session.json").Files);
    }

    [Fact]
    public void ShouldWriteBom_OnlyForPlainText()
    {
        var s = new AppSettings();
        Assert.True(s.ShouldWriteBom(".md"));
        Assert.True(s.ShouldWriteBom(".TXT"));
        Assert.False(s.ShouldWriteBom(".cs"));
        Assert.False(s.ShouldWriteBom(".json"));
        s.Utf8BomForPlainText = false;
        Assert.False(s.ShouldWriteBom(".md"));
    }
}
