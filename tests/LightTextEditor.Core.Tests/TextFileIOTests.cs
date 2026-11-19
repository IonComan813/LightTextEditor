using System.Text;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class TextFileIOTests
{
    static TextFileIOTests() => TextEncoding.EnsureCodePagesRegistered();

    [Fact]
    public void Utf8WithBom_IsDetected()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("你好")).ToArray();
        var r = TextFileIO.Decode(bytes);
        Assert.Equal("你好", r.Text);
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.True(r.Encoding.HasBom);
        Assert.True(r.Encoding.IsCertain);
        Assert.Equal("UTF-8 with BOM", r.Encoding.DisplayName);
    }

    [Fact]
    public void Utf8WithoutBom_IsDetectedByStrictValidation()
    {
        var r = TextFileIO.Decode(Encoding.UTF8.GetBytes("中文 text"));
        Assert.Equal("中文 text", r.Text);
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.False(r.Encoding.HasBom);
        Assert.True(r.Encoding.IsCertain);
    }

    [Theory]
    [InlineData(1200)]  // UTF-16 LE
    [InlineData(1201)]  // UTF-16 BE
    [InlineData(12000)] // UTF-32 LE
    [InlineData(12001)] // UTF-32 BE
    public void UnicodeBoms_AreDetected(int codePage)
    {
        var enc = Encoding.GetEncoding(codePage);
        var bytes = enc.GetPreamble().Concat(enc.GetBytes("Hello 世界")).ToArray();
        var r = TextFileIO.Decode(bytes);
        Assert.Equal("Hello 世界", r.Text);
        Assert.Equal(codePage, r.Encoding.CodePage);
        Assert.True(r.Encoding.HasBom);
        Assert.False(r.LooksBinary);
    }

    [Fact]
    public void Gbk_FallsBackToGb18030_AndIsUncertain()
    {
        var gbk = Encoding.GetEncoding(936).GetBytes("中文测试，编码检测");
        var r = TextFileIO.Decode(gbk);
        Assert.Equal("中文测试，编码检测", r.Text);
        Assert.Equal(54936, r.Encoding.CodePage);
        Assert.False(r.Encoding.IsCertain);
    }

    [Fact]
    public void EmptyFile_IsUtf8()
    {
        var r = TextFileIO.Decode([]);
        Assert.Equal("", r.Text);
        Assert.Equal(65001, r.Encoding.CodePage);
    }

    [Fact]
    public void NulBytes_AreFlaggedAsBinary()
    {
        var r = TextFileIO.Decode([0x41, 0x00, 0x42, 0x01]);
        Assert.True(r.LooksBinary);
    }

    [Fact]
    public void Encode_WritesBomOnlyWhenRequested()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, TextFileIO.Encode("a", TextEncoding.Utf8(true)));
        Assert.Equal(new byte[] { 0x61 }, TextFileIO.Encode("a", TextEncoding.Utf8(false)));
    }

    [Fact]
    public void Encode_ThrowsWhenEncodingCannotRepresentText()
    {
        var latin1 = new TextEncoding(Encoding.Latin1, false, true);
        var ex = Assert.Throws<EncodingLossException>(() => TextFileIO.Encode("abc中", latin1));
        Assert.Equal('中', ex.Character);
        Assert.Equal(3, ex.Index);
    }

    [Fact]
    public void Gb18030_RoundTrips()
    {
        var enc = TextEncoding.Gb18030(true);
        const string text = "中文 😀 繁體";
        var bytes = TextFileIO.Encode(text, enc);
        Assert.Equal(text, enc.Encoding.GetString(bytes));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("note.md");
        TextFileIO.Save(path, "第一版\r\n", TextEncoding.Utf8(true));
        TextFileIO.Save(path, "第二版\r\n内容", TextEncoding.Utf8(true));

        var r = TextFileIO.Load(path);
        Assert.Equal("第二版\r\n内容", r.Text);
        Assert.True(r.Encoding.HasBom);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Save_FailsOnReadOnlyFile_AndKeepsOriginal()
    {
        using var dir = new TempDir();
        var path = dir.File("ro.txt");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => TextFileIO.Save(path, "new", TextEncoding.Utf8(false)));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "a.txt");
        TextFileIO.Save(path, "x", TextEncoding.Utf8(false));
        Assert.Equal("x", File.ReadAllText(path));
    }
}
