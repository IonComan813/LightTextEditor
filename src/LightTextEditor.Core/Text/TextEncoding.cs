using System.Text;

namespace LightTextEditor.Core.Text;

/// <summary>
/// 一个文件使用的编码：编码本身 + 是否带 BOM + 是否已确定（检测可靠或用户已确认）。
/// </summary>
public sealed record TextEncoding(Encoding Encoding, bool HasBom, bool IsCertain)
{
    static TextEncoding()
    {
        // .NET (Core) 默认只带 Unicode 编码，GBK / Big5 / Shift-JIS 等需要注册代码页提供程序。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>确保代码页提供程序已注册（访问静态成员即可触发静态构造函数）。</summary>
    public static void EnsureCodePagesRegistered() { }

    public static TextEncoding Utf8(bool withBom) =>
        new(new UTF8Encoding(false, true), withBom, IsCertain: true);

    public static TextEncoding Gb18030(bool isCertain) =>
        new(Encoding.GetEncoding(54936), false, isCertain);

    /// <summary>
    /// 按代码页创建编码。Unicode 编码不抛出解码异常（无效字节显示为 U+FFFD），
    /// 是否带 BOM 由 <paramref name="hasBom"/> 决定。
    /// </summary>
    public static TextEncoding FromCodePage(int codePage, bool hasBom = false, bool isCertain = true)
    {
        EnsureCodePagesRegistered();
        Encoding encoding = codePage switch
        {
            65001 => new UTF8Encoding(false, true),
            1200 => new UnicodeEncoding(false, false),
            1201 => new UnicodeEncoding(true, false),
            12000 => new UTF32Encoding(false, false),
            12001 => new UTF32Encoding(true, false),
            _ => Encoding.GetEncoding(codePage),
        };
        return new TextEncoding(encoding, hasBom && IsUnicodeCodePage(codePage), isCertain);
    }

    /// <summary>
    /// 把编码名（如 "gbk"、"shift_jis"、"euc-kr"，来自统计检测或 .editorconfig）转换为编码。
    /// 简体中文统一用 GB18030（GBK / GB2312 的超集），韩文用 CP949（EUC-KR 的超集）。不认识时返回 null。
    /// </summary>
    public static TextEncoding? FromName(string name, bool isCertain = true)
    {
        EnsureCodePagesRegistered();
        int? codePage = name.Trim().ToLowerInvariant() switch
        {
            "utf-8" or "utf8" or "ascii" or "us-ascii" => 65001,
            "gb18030" or "gbk" or "gb2312" or "cp936" or "x-gbk" => 54936,
            "big5" or "big5-hkscs" or "cp950" => 950,
            "shift-jis" or "shift_jis" or "sjis" or "cp932" or "windows-31j" => 932,
            "euc-kr" or "cp949" or "ks_c_5601-1987" => 949,
            "euc-jp" => 51932,
            "utf-16le" or "utf-16" => 1200,
            "utf-16be" => 1201,
            "utf-32le" or "utf-32" => 12000,
            "utf-32be" => 12001,
            "latin1" or "iso-8859-1" => 28591,
            _ => null,
        };
        if (codePage is { } cp) return FromCodePage(cp, isCertain: isCertain);
        try
        {
            return new TextEncoding(Encoding.GetEncoding(name), false, isCertain);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUnicodeCodePage(int codePage) => codePage is 65001 or 1200 or 1201 or 12000 or 12001;

    public int CodePage => Encoding.CodePage;

    public bool IsUnicode => IsUnicodeCodePage(CodePage);

    public bool IsUtf8 => CodePage == 65001;

    /// <summary>状态栏上显示的名称，如 "UTF-8"、"UTF-8 with BOM"、"GB18030"。</summary>
    public string DisplayName
    {
        get
        {
            string name = CodePage switch
            {
                65001 => "UTF-8",
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                12000 => "UTF-32 LE",
                12001 => "UTF-32 BE",
                54936 => "GB18030",
                936 => "GBK",
                950 => "Big5",
                932 => "Shift-JIS",
                949 or 51949 => "EUC-KR",
                51932 => "EUC-JP",
                20866 => "KOI8-R",
                28591 => "ISO-8859-1",
                >= 1250 and <= 1258 => "Windows-" + CodePage,
                _ => Encoding.WebName.ToUpperInvariant(),
            };
            return HasBom && CodePage == 65001 ? name + " with BOM" : name;
        }
    }

    /// <summary>编码适用的语言（"简体中文"、"日文"……），用于提示；没有时为 null。</summary>
    public string? LanguageHint => CodePage switch
    {
        54936 or 936 => "简体中文",
        950 => "繁体中文",
        932 or 51932 => "日文",
        949 or 51949 => "韩文",
        1250 or 28592 => "中欧",                 // ISO-8859-2
        1251 or 20866 or 28595 => "西里尔文",    // KOI8-R、ISO-8859-5
        21866 => "乌克兰文",                     // KOI8-U
        1252 or 28591 or 28605 => "西欧",        // ISO-8859-1、ISO-8859-15
        28593 => "南欧",                         // ISO-8859-3（马耳他文、世界语）
        28594 or 28603 => "波罗的海",            // ISO-8859-4、ISO-8859-13
        1253 or 28597 => "希腊文",               // ISO-8859-7
        1254 or 28599 => "土耳其文",             // ISO-8859-9
        1255 or 28598 or 38598 => "希伯来文",    // ISO-8859-8、ISO-8859-8-I
        1256 or 28596 => "阿拉伯文",             // ISO-8859-6
        1257 => "波罗的海",
        1258 => "越南文",
        874 => "泰文",
        _ => null,
    };

    /// <summary>带语言说明的名称，如 "GB18030（简体中文）"。</summary>
    public string Description => LanguageHint is { } hint ? $"{DisplayName}（{hint}）" : DisplayName;

    /// <summary>该编码对应的 BOM 字节（不论 <see cref="HasBom"/> 是否为真）。</summary>
    public byte[] BomBytes => CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        _ => [],
    };

    public bool Equals(TextEncoding? other) =>
        other is not null && CodePage == other.CodePage && HasBom == other.HasBom && IsCertain == other.IsCertain;

    public override int GetHashCode() => HashCode.Combine(CodePage, HasBom, IsCertain);
}
