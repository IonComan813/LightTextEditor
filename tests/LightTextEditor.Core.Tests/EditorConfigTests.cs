using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class EditorConfigTests
{
    [Theory]
    [InlineData("*", "a.txt", true)]
    [InlineData("*", "sub/a.txt", true)]
    [InlineData("*.md", "notes/x.md", true)]
    [InlineData("*.md", "x.txt", false)]
    [InlineData("*.{cs,md}", "x.cs", true)]
    [InlineData("*.{cs,md}", "x.txt", false)]
    [InlineData("/root.txt", "root.txt", true)]
    [InlineData("/root.txt", "sub/root.txt", false)]
    [InlineData("lib/**.js", "lib/a/b/c.js", true)]
    [InlineData("lib/*.js", "lib/a/c.js", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("[ab].txt", "b.txt", true)]
    [InlineData("[!ab].txt", "b.txt", false)]
    [InlineData("v{1..3}.txt", "v2.txt", true)]
    [InlineData("v{1..3}.txt", "v4.txt", false)]
    public void Globs_Match(string glob, string relativePath, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "ec-root");
        Assert.Equal(expected, EditorConfig.Matches(glob, root, Path.Combine(root, relativePath)));
    }

    [Fact]
    public void Charset_ComesFromNearestFile_AndLaterSectionsWin()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".editorconfig"), "root = true\n[*]\ncharset = utf-8\n[*.txt]\ncharset = latin1\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(dir.Path, "sub", ".editorconfig"), "[*.md]\ncharset = utf-8-bom\n");

        Assert.Equal("latin1", EditorConfig.GetCharset(dir.File("a.txt")));
        Assert.Equal("utf-8", EditorConfig.GetCharset(dir.File("a.cs")));
        Assert.Equal("utf-8-bom", EditorConfig.GetCharset(Path.Combine(dir.Path, "sub", "n.md")));
        Assert.Equal("latin1", EditorConfig.GetCharset(Path.Combine(dir.Path, "sub", "n.txt")));
    }

    [Fact]
    public void Root_StopsTheSearch()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".editorconfig"), "[*]\ncharset = latin1\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "proj"));
        File.WriteAllText(Path.Combine(dir.Path, "proj", ".editorconfig"), "root=true\n# comment\n; also comment\n[*.cs]\nindent_style = space\n");

        Assert.Null(EditorConfig.GetCharset(Path.Combine(dir.Path, "proj", "a.txt")));
        Assert.Equal("latin1", EditorConfig.GetCharset(dir.File("a.txt")));
    }

    [Fact]
    public void Unset_AndUnknownCharsets_AreIgnored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".editorconfig"), "root = true\n[*.a]\ncharset = unset\n[*.b]\ncharset = klingon\n");
        Assert.Null(EditorConfig.GetCharset(dir.File("x.a")));
        Assert.Null(EditorConfig.GetEncoding(dir.File("x.b")));
    }

    [Fact]
    public void Load_UsesEditorConfig()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(".editorconfig"), "root = true\n[*.txt]\ncharset = latin1\n");
        File.WriteAllBytes(dir.File("a.txt"), [0x63, 0x61, 0x66, 0xE9]);
        var r = TextFileIO.Load(dir.File("a.txt"));
        Assert.Equal("café", r.Text);
        Assert.Equal(DetectionMethod.EditorConfig, r.Detection.Method);
    }
}
