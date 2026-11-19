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

    public int CodePage => Encoding.CodePage;

    public bool IsUnicode => CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

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
                _ => Encoding.WebName.ToUpperInvariant(),
            };
            return HasBom && CodePage == 65001 ? name + " with BOM" : name;
        }
    }

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
