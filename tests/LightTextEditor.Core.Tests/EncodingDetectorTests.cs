using System.Text;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

/// <summary>
/// 编码检测的样本集：各种旧编码（GBK / Big5 / Shift-JIS / EUC-KR / 西欧 / 西里尔）、无 BOM 的 UTF-16、短文本，
/// 以及编码记录、.editorconfig、无法识别的字节等情况。样本在测试里由字符串按对应编码生成。
/// </summary>
public class EncodingDetectorTests
{
    static EncodingDetectorTests() => TextEncoding.EnsureCodePagesRegistered();

    private static byte[] Bytes(string text, int codePage) => Encoding.GetEncoding(codePage).GetBytes(text);

    public static TheoryData<string, string, int, int> LongSamples => new()
    {
        // 名称, 文本, 生成样本用的代码页, 期望检测出的代码页
        { "GBK", "这是一个用于测试编码检测的简体中文段落。它包含常用的汉字、标点符号，以及数字 12345 和英文 words。", 936, 54936 },
        { "GB2312 短句", "今天天气不错，我们去公园散步吧。", 936, 54936 },
        { "Big5", "這是一段繁體中文的測試文字，用來檢查編碼偵測是否正確。", 950, 950 },
        { "Shift-JIS", "これは文字コード判定のテスト用の日本語の文章です。ひらがな、カタカナ、漢字を含みます。", 932, 932 },
        { "Shift-JIS 短", "日本語のテキスト", 932, 932 },
        { "EUC-KR", "한국어 텍스트입니다. 인코딩 감지 테스트를 위한 문장입니다.", 949, 949 },
    };

    [Theory]
    [MemberData(nameof(LongSamples))]
    public void LegacyEncodings_AreDetectedStatistically(string name, string text, int sourceCodePage, int expectedCodePage)
    {
        var r = TextFileIO.Decode(Bytes(text, sourceCodePage));
        Assert.True(expectedCodePage == r.Encoding.CodePage, $"{name}: {r.Encoding.DisplayName} via {r.Detection.Method}");
        Assert.Equal(text, r.Text);
        Assert.Equal(DetectionMethod.Statistical, r.Detection.Method);
        Assert.True(r.Encoding.IsCertain, $"{name}: confidence {r.Detection.Confidence}");
        Assert.True(r.Detection.IsLossless);
        Assert.False(r.LooksBinary);
    }

    [Theory]
    [InlineData("中文")]
    [InlineData("你好世界")]
    [InlineData("int main() { printf(\"你好\"); }")]
    [InlineData("// 注释\nvar x = 1;")]
    public void ShortGbkText_IsGuessedAsGb18030_AndNeedsConfirmation(string text)
    {
        var r = TextFileIO.Decode(Bytes(text, 936));
        Assert.Equal(54936, r.Encoding.CodePage);
        Assert.Equal(text, r.Text);
        Assert.False(r.Encoding.IsCertain);
        Assert.Equal(DetectionMethod.CjkHeuristic, r.Detection.Method);
    }

    [Fact]
    public void ShortBig5Text_IsGuessedAsBig5()
    {
        var r = TextFileIO.Decode(Bytes("繁體中文", 950));
        Assert.Equal(950, r.Encoding.CodePage);
        Assert.Equal("繁體中文", r.Text);
        Assert.False(r.Encoding.IsCertain);
    }

    [Theory]
    [InlineData("Café crème brûlée, naïve façade — déjà vu.", 1252)]
    [InlineData("Привет, мир! Это тест кодировки.", 1251)]
    public void SingleByteEncodings_AreDetectedButNotCertain(string text, int codePage)
    {
        var r = TextFileIO.Decode(Bytes(text, codePage));
        Assert.Equal(codePage, r.Encoding.CodePage);
        Assert.Equal(text, r.Text);
        Assert.False(r.Encoding.IsCertain);
        Assert.Equal(DetectionMethod.Statistical, r.Detection.Method);
        Assert.NotNull(r.Detection.ConfidenceText);
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(1201)]
    public void Utf16WithoutBom_IsDetected_AndNotBinary(int codePage)
    {
        const string text = "Hello, world!\r\nThis is UTF-16 without a BOM. 中文也可以。\r\n";
        var r = TextFileIO.Decode(Bytes(text, codePage));
        Assert.Equal(codePage, r.Encoding.CodePage);
        Assert.False(r.Encoding.HasBom);
        Assert.Equal(DetectionMethod.Utf16Heuristic, r.Detection.Method);
        Assert.Equal(text, r.Text);
        Assert.False(r.LooksBinary);
    }

    [Fact]
    public void Utf16WithoutBom_RoundTripsWithoutAddingBom()
    {
        var bytes = Bytes("plain ascii in utf-16", 1200);
        var r = TextFileIO.Decode(bytes);
        Assert.Equal(bytes, TextFileIO.Encode(r.Text, r.Encoding));
    }

    [Fact]
    public void AsciiAndUtf8_AreCertainUtf8()
    {
        Assert.Equal(DetectionMethod.Utf8, TextFileIO.Decode("just ascii"u8.ToArray()).Detection.Method);
        var r = TextFileIO.Decode(Encoding.UTF8.GetBytes("中文 UTF-8"));
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.True(r.Encoding.IsCertain);
    }

    [Fact]
    public void BinaryData_IsNotMistakenForUtf16()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
        var r = TextFileIO.Decode(bytes);
        Assert.NotEqual(DetectionMethod.Utf16Heuristic, r.Detection.Method);
        Assert.True(r.LooksBinary);
    }

    // ───────────── 编码记录 ─────────────

    [Fact]
    public void RememberedEncoding_WinsOverStatisticalGuess()
    {
        // 这段 Big5 字节按统计检测是 Big5；用户曾确认为 GB18030（假设）时按记录打开，且确定
        var bytes = Bytes("這是一段繁體中文的測試文字，用來檢查編碼偵測是否正確。", 950);
        var r = TextFileIO.Decode(bytes, remembered: TextEncoding.Gb18030(true));
        Assert.Equal(54936, r.Encoding.CodePage);
        Assert.Equal(DetectionMethod.Remembered, r.Detection.Method);
        Assert.True(r.Encoding.IsCertain);
    }

    [Fact]
    public void RememberedEncoding_AppliesToPureAscii()
    {
        // 文件现在只有 ASCII，但用户确认过是 GBK：保持 GBK（以后输入中文时仍按 GBK 保存）
        var r = TextFileIO.Decode("ascii only"u8.ToArray(), remembered: TextEncoding.Gb18030(true));
        Assert.Equal(54936, r.Encoding.CodePage);
    }

    [Fact]
    public void RememberedEncoding_IsIgnored_WhenFileWasConvertedToUtf8()
    {
        var r = TextFileIO.Decode(Encoding.UTF8.GetBytes("已经被别的程序转成 UTF-8"), remembered: TextEncoding.Gb18030(true));
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.Equal(DetectionMethod.Utf8, r.Detection.Method);
    }

    [Fact]
    public void Bom_WinsOverRememberedEncoding()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("中文")).ToArray();
        var r = TextFileIO.Decode(bytes, remembered: TextEncoding.Gb18030(true));
        Assert.Equal(DetectionMethod.Bom, r.Detection.Method);
        Assert.Equal("中文", r.Text);
    }

    // ───────────── .editorconfig ─────────────

    [Fact]
    public void EditorConfigLatin1_IsUsed()
    {
        var r = TextFileIO.Decode(Bytes("Café", 28591), editorConfigCharset: "latin1");
        Assert.Equal(28591, r.Encoding.CodePage);
        Assert.Equal(DetectionMethod.EditorConfig, r.Detection.Method);
        Assert.Equal("Café", r.Text);
    }

    [Fact]
    public void EditorConfigUtf8_IsIgnored_WhenContentIsNotUtf8()
    {
        var r = TextFileIO.Decode(Bytes("这是一个用于测试编码检测的简体中文段落，内容足够长以便统计检测。", 936), editorConfigCharset: "utf-8");
        Assert.Equal(54936, r.Encoding.CodePage);
    }

    [Fact]
    public void EditorConfigUtf8Bom_AddsBomOnSave()
    {
        var r = TextFileIO.Decode("abc"u8.ToArray(), editorConfigCharset: "utf-8-bom");
        Assert.True(r.Encoding.HasBom);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x62, 0x63 }, TextFileIO.Encode(r.Text, r.Encoding));
    }

    // ───────────── 无法识别的字节 ─────────────

    [Fact]
    public void InvalidBytesAfterUtf8Bom_AreReplaced_AndReportedAsLossy()
    {
        var r = TextFileIO.Decode([0xEF, 0xBB, 0xBF, 0x61, 0xFF, 0x62]);
        Assert.Equal("a�b", r.Text);
        Assert.False(r.Detection.IsLossless);
    }

    [Fact]
    public void DecodeAs_ReportsWhetherBytesSurvive()
    {
        var gbk = Bytes("简体中文内容", 936);
        Assert.False(TextFileIO.DecodeAs(gbk, TextEncoding.Utf8(false)).Detection.IsLossless);
        var asGb = TextFileIO.DecodeAs(gbk, TextEncoding.Gb18030(true));
        Assert.True(asGb.Detection.IsLossless);
        Assert.Equal("简体中文内容", asGb.Text);
    }

    [Fact]
    public void LargeFile_IsDetectedFromSample()
    {
        var sb = new StringBuilder();
        while (sb.Length < 400_000) sb.Append("这是一个很长的简体中文文件，用来测试只检测开头部分的情况。\r\n");
        var r = TextFileIO.Decode(Bytes(sb.ToString(), 936));
        Assert.Equal(54936, r.Encoding.CodePage);
        Assert.Equal(sb.ToString(), r.Text);
    }

    [Fact]
    public void Candidates_ListEncodingsConsidered_EndingWithTheChosenOne()
    {
        // 4 字节 GBK：UTF-8 校验失败 → 统计检测 KOI8-R（置信度太低）→ 结构推测 GB18030
        Assert.Equal([65001, 20866, 54936], TextFileIO.Decode(Bytes("中文", 936)).Detection.Candidates);
        // 较长的 GBK：UTF-8 校验失败 → 统计检测 GB18030
        Assert.Equal([65001, 54936], TextFileIO.Decode(Bytes("这是一个用于测试编码检测的简体中文段落，内容足够长以便统计检测。", 936)).Detection.Candidates);
        // 确定的结果只有它自己
        Assert.Equal([65001], TextFileIO.Decode("ascii"u8.ToArray()).Detection.Candidates);
        // .editorconfig 说是 UTF-8 但内容不是：UTF-8 只记一次
        Assert.Equal([65001, 54936], TextFileIO.Decode(Bytes("这是一个用于测试编码检测的简体中文段落，内容足够长以便统计检测。", 936), editorConfigCharset: "utf-8").Detection.Candidates);
    }

    [Theory]
    [InlineData("gbk", 54936)]
    [InlineData("GB2312", 54936)]
    [InlineData("shift_jis", 932)]
    [InlineData("euc-kr", 949)]
    [InlineData("big5", 950)]
    [InlineData("windows-1251", 1251)]
    public void FromName_MapsToSupersets(string name, int codePage) =>
        Assert.Equal(codePage, TextEncoding.FromName(name)!.CodePage);

    [Fact]
    public void Description_IncludesLanguage()
    {
        Assert.Equal("GB18030（简体中文）", TextEncoding.Gb18030(true).Description);
        Assert.Equal("Big5（繁体中文）", TextEncoding.FromCodePage(950).Description);
        Assert.Equal("UTF-8", TextEncoding.Utf8(false).Description);
    }

    [Theory]
    [InlineData(28591, "ISO-8859-1（西欧）")]
    [InlineData(28592, "ISO-8859-2（中欧）")]
    [InlineData(28593, "ISO-8859-3（南欧）")]
    [InlineData(28594, "ISO-8859-4（波罗的海）")]
    [InlineData(28595, "ISO-8859-5（西里尔文）")]
    [InlineData(28596, "ISO-8859-6（阿拉伯文）")]
    [InlineData(28597, "ISO-8859-7（希腊文）")]
    [InlineData(28598, "ISO-8859-8（希伯来文）")]
    [InlineData(28599, "ISO-8859-9（土耳其文）")]
    [InlineData(28603, "ISO-8859-13（波罗的海）")]
    [InlineData(28605, "ISO-8859-15（西欧）")]
    [InlineData(21866, "KOI8-U（乌克兰文）")]
    public void Iso8859Family_HasLanguageHints(int codePage, string expected)
    {
        Assert.Equal(expected, TextEncoding.FromCodePage(codePage).Description);
    }
}
