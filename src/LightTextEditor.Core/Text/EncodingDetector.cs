using System.Text;
using UtfUnknown;

namespace LightTextEditor.Core.Text;

/// <summary>编码是怎么得出的。</summary>
public enum DetectionMethod
{
    /// <summary>文件开头的 BOM。</summary>
    Bom,
    /// <summary>用户之前确认 / 选择过的编码（编码记录）。</summary>
    Remembered,
    /// <summary><c>.editorconfig</c> 的 <c>charset</c>。</summary>
    EditorConfig,
    /// <summary>通过了严格的 UTF-8 校验（含纯 ASCII 和空文件）。</summary>
    Utf8,
    /// <summary>没有 BOM 的 UTF-16：大量 0x00 交替出现。</summary>
    Utf16Heuristic,
    /// <summary>统计检测（UTF.Unknown）。</summary>
    Statistical,
    /// <summary>统计检测不可靠时，按中文双字节编码的结构推测。</summary>
    CjkHeuristic,
    /// <summary>什么都判断不出来，按 GB18030 打开。</summary>
    Fallback,
    /// <summary>用户指定的编码（"以其他编码重新打开"）。</summary>
    Manual,
}

/// <summary>
/// 编码检测结果。
/// <paramref name="Confidence"/>：0~1，统计检测给出的置信度；其他方式为 1（推测时为 0）。
/// <paramref name="IsLossless"/>：用该编码解码后再编码能否得到完全相同的字节；为 false 时说明有字节无法识别，
/// 按此编码保存会改变这些字节。
/// </summary>
public sealed record EncodingDetection(TextEncoding Encoding, DetectionMethod Method, double Confidence, bool IsLossless)
{
    /// <summary>
    /// 检测过程中考虑过的编码（代码页），最后一个是采用的编码。
    /// 例如 GBK 短文本：[UTF-8（校验失败）, 统计检测给出的 KOI8-R（置信度太低）, GB18030]。
    /// 界面上"更换编码重试"的列表把它们显示为灰色。没有设置时只有采用的编码。
    /// </summary>
    public IReadOnlyList<int> Candidates
    {
        get => _candidates ?? [Encoding.CodePage];
        init => _candidates = value;
    }

    private readonly IReadOnlyList<int>? _candidates;

    /// <summary>适合显示的置信度，如 "62%"；不是统计检测时为 null。</summary>
    public string? ConfidenceText => Method == DetectionMethod.Statistical ? $"{Math.Round(Confidence * 100)}%" : null;
}

/// <summary>
/// 编码识别管线：BOM → 编码记录 / .editorconfig → 无 BOM 的 UTF-16 → 严格 UTF-8 → 统计检测（UTF.Unknown）
/// → 中文双字节结构推测 → 回退为 GB18030。
/// 结果不可靠时 <see cref="TextEncoding.IsCertain"/> 为 false，界面上显示"待确认"提示。
/// </summary>
public static class EncodingDetector
{
    /// <summary>
    /// 统计检测的置信度不低于此值、且能无损往返时，视为确定；低于此值时（短文本常见）先尝试中文双字节结构推测。
    /// </summary>
    public const double CertainConfidence = 0.9;

    /// <summary>参与检测的最大字节数（大文件只看开头）。</summary>
    private const int SampleSize = 256 * 1024;

    /// <param name="bytes">文件内容。</param>
    /// <param name="remembered">编码记录中该文件已确认的编码。</param>
    /// <param name="editorConfigCharset"><c>.editorconfig</c> 中的 <c>charset</c>。</param>
    public static EncodingDetection Detect(byte[] bytes, TextEncoding? remembered = null, string? editorConfigCharset = null)
    {
        TextEncoding.EnsureCodePagesRegistered();

        // 1. BOM
        if (DetectBom(bytes) is { } bom)
            return new EncodingDetection(bom, DetectionMethod.Bom, 1, RoundTrips(bytes, bom));

        bool isAscii = IsAscii(bytes);
        bool isValidUtf8 = isAscii || IsValidUtf8(bytes);

        // 2. 编码记录：用户确认过的编码优先。
        //    但如果文件现在是含非 ASCII 字符的合法 UTF-8，而记录的是其他编码，说明文件已被别的程序转码，记录作废。
        if (remembered is not null && (!remembered.IsUnicode || remembered.IsUtf8))
        {
            bool stale = !remembered.IsUtf8 && isValidUtf8 && !isAscii;
            if (!stale)
            {
                var enc = remembered.IsUtf8 ? TextEncoding.Utf8(withBom: false) : remembered with { HasBom = false, IsCertain = true };
                return new EncodingDetection(enc, DetectionMethod.Remembered, 1, RoundTrips(bytes, enc));
            }
        }

        // 2'. .editorconfig：只在文件内容与之相符时采用
        if (editorConfigCharset is not null && FromEditorConfig(bytes, editorConfigCharset, isValidUtf8) is { } configured)
            return new EncodingDetection(configured, DetectionMethod.EditorConfig, 1, true);

        // 3. 无 BOM 的 UTF-16（ASCII 为主的 UTF-16 文本同时也是"合法 UTF-8"，所以必须在 UTF-8 之前判断）
        if (DetectUtf16WithoutBom(bytes) is { } utf16)
            return new EncodingDetection(utf16, DetectionMethod.Utf16Heuristic, 1, true);

        // 4. 严格 UTF-8（纯 ASCII、空文件也在这里）
        if (isValidUtf8)
            return new EncodingDetection(TextEncoding.Utf8(withBom: false), DetectionMethod.Utf8, 1, true);

        // 以下各步的结果都可能"待确认"：记下考虑过的编码，供用户更换编码时参考
        var candidates = new List<int> { 65001 }; // UTF-8 校验已失败
        if (editorConfigCharset is not null && EditorConfig.CharsetToEncoding(editorConfigCharset) is { } rejected)
            Consider(rejected.CodePage);
        void Consider(int codePage)
        {
            if (!candidates.Contains(codePage)) candidates.Add(codePage);
        }
        EncodingDetection Result(TextEncoding enc, DetectionMethod method, double conf, bool lossless)
        {
            candidates.Remove(enc.CodePage);
            candidates.Add(enc.CodePage);
            return new EncodingDetection(enc, method, conf, lossless) { Candidates = candidates };
        }

        // 5. 统计检测
        var sample = bytes.Length > SampleSize ? bytes.AsSpan(0, TrimToCharBoundary(bytes, SampleSize)).ToArray() : bytes;
        var (statistical, confidence) = DetectStatistically(sample);
        if (statistical is not null)
        {
            if (confidence >= CertainConfidence && RoundTrips(bytes, statistical))
                return Result(statistical, DetectionMethod.Statistical, confidence, true);
            Consider(statistical.CodePage);
        }

        // 6. 统计检测不可靠（多为短文本）：按双字节结构推测简体 / 繁体中文，再退回统计检测的结果
        var stats = CjkStats.Analyze(sample);
        if (stats.LooksLikeGb2312)
        {
            var gb = TextEncoding.Gb18030(isCertain: false);
            if (RoundTrips(bytes, gb)) return Result(gb, DetectionMethod.CjkHeuristic, 0, true);
            Consider(gb.CodePage);
        }
        if (stats.LooksLikeBig5)
        {
            var big5 = TextEncoding.FromCodePage(950, isCertain: false);
            if (RoundTrips(bytes, big5)) return Result(big5, DetectionMethod.CjkHeuristic, 0, true);
            Consider(big5.CodePage);
        }
        if (statistical is not null && RoundTrips(bytes, statistical))
            return Result(statistical with { IsCertain = false }, DetectionMethod.Statistical, confidence, true);

        // 7. 回退
        var fallback = TextEncoding.Gb18030(isCertain: false);
        return Result(fallback, DetectionMethod.Fallback, 0, RoundTrips(bytes, fallback));
    }

    /// <summary>识别 BOM；没有时返回 null。</summary>
    public static TextEncoding? DetectBom(ReadOnlySpan<byte> b)
    {
        // UTF-32 LE 必须在 UTF-16 LE 之前判断（前两个字节相同）。
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00])) return TextEncoding.FromCodePage(12000, hasBom: true);
        if (b.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF])) return TextEncoding.FromCodePage(12001, hasBom: true);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return TextEncoding.Utf8(withBom: true);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return TextEncoding.FromCodePage(1200, hasBom: true);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return TextEncoding.FromCodePage(1201, hasBom: true);
        return null;
    }

    /// <summary>用该编码解码再编码，能否得到完全相同的字节（不含 BOM）。</summary>
    public static bool RoundTrips(byte[] bytes, TextEncoding encoding)
    {
        try
        {
            int skip = encoding.HasBom && bytes.AsSpan().StartsWith(encoding.BomBytes) ? encoding.BomBytes.Length : 0;
            var decoder = (Encoding)encoding.Encoding.Clone();
            decoder.DecoderFallback = DecoderFallback.ExceptionFallback;
            decoder.EncoderFallback = EncoderFallback.ExceptionFallback;
            var text = decoder.GetString(bytes, skip, bytes.Length - skip);
            return decoder.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(skip));
        }
        catch (Exception e) when (e is DecoderFallbackException or EncoderFallbackException)
        {
            return false;
        }
    }

    private static TextEncoding? FromEditorConfig(byte[] bytes, string charset, bool isValidUtf8)
    {
        var enc = EditorConfig.CharsetToEncoding(charset);
        if (enc is null) return null;
        if (enc.IsUtf8) return isValidUtf8 ? enc : null;
        if (enc.CodePage is 1200 or 1201)
            return bytes.Length % 2 == 0 && RoundTrips(bytes, enc) ? enc : null;
        return enc; // latin1：任何字节都能解码
    }

    private static TextEncoding? DetectUtf16WithoutBom(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, SampleSize) & ~1;
        if (n < 8 || bytes.Length % 2 != 0) return null;

        int pairs = n / 2, zeroEven = 0, zeroOdd = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (bytes[i] == 0) zeroEven++;
            if (bytes[i + 1] == 0) zeroOdd++;
        }

        // 以 ASCII 为主的 UTF-16：一侧大量是 0x00，另一侧几乎没有。
        int? codePage = null;
        if (zeroOdd >= pairs * 0.4 && zeroEven <= pairs * 0.05) codePage = 1200;
        else if (zeroEven >= pairs * 0.4 && zeroOdd <= pairs * 0.05) codePage = 1201;
        if (codePage is null) return null;

        var enc = TextEncoding.FromCodePage(codePage.Value);
        if (!RoundTrips(bytes, enc)) return null;

        // 文本里不应有控制字符（制表、换行、换页除外）：0x00 交替出现的二进制数据（如 16 位整数数组）通常会有
        var text = enc.Encoding.GetString(bytes, 0, n);
        int controls = text.Count(c => c < 0x20 && c is not ('\t' or '\n' or '\r' or '\f'));
        return controls <= text.Length / 200 ? enc : null;
    }

    private static (TextEncoding? Encoding, double Confidence) DetectStatistically(byte[] sample)
    {
        try
        {
            var detected = CharsetDetector.DetectFromBytes(sample).Detected;
            if (detected?.EncodingName is not { } name) return (null, 0);
            var enc = TextEncoding.FromName(name);
            // 统计检测不应得出 UTF-8 / UTF-16（前面已经判断过）
            return enc is null || enc.IsUnicode ? (null, 0) : (enc, detected.Confidence);
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    private static bool IsAscii(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b >= 0x80) return false;
        return true;
    }

    private static bool IsValidUtf8(byte[] bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    /// <summary>截取样本时不要从多字节字符中间切断（向前退到 ASCII 字节之后）。</summary>
    private static int TrimToCharBoundary(byte[] bytes, int length)
    {
        int i = length;
        while (i > length - 8 && i > 0 && bytes[i - 1] >= 0x80) i--;
        return i > length - 8 ? i : length;
    }

    /// <summary>双字节编码的结构统计：用来在短文本上区分 GB2312/GBK 与 Big5。</summary>
    internal readonly record struct CjkStats(int Pairs, int Gb2312Pairs, int CommonPairs, int Big5Pairs, int Big5LowTrail, int Invalid)
    {
        /// <summary>
        /// 简体中文的常用字几乎都在 GB2312 范围内（首字节 A1–F7，尾字节 A1–FE），
        /// 另外不应出现 A4 / A5 行（GB2312 中的日文假名）——繁体 Big5 文本按 GBK 解读时常落在这两行。
        /// 并且大部分应是标点（A1–A3 行）或一级常用字（B0–D7 行）：西里尔文等单字节文本恰好两两成对时，
        /// 首字节分布均匀，大多落在 D8 以后的二级字区。
        /// </summary>
        public bool LooksLikeGb2312 => Pairs > 0 && Invalid == 0 && Gb2312Pairs == Pairs && CommonPairs >= Pairs * 0.8;

        /// <summary>Big5 约四成的字尾字节在 40–7E（GB2312 中不存在）。</summary>
        public bool LooksLikeBig5 => Pairs > 0 && Invalid == 0 && Big5Pairs == Pairs && Big5LowTrail > 0;

        public static CjkStats Analyze(byte[] bytes)
        {
            int pairs = 0, gb = 0, common = 0, big5 = 0, big5Low = 0, invalid = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte lead = bytes[i];
                if (lead < 0x80) continue;
                if (i + 1 >= bytes.Length) { invalid++; break; }
                byte trail = bytes[++i];
                if (lead is < 0x81 or > 0xFE || trail < 0x40 || trail == 0x7F || trail == 0xFF) { invalid++; continue; }

                pairs++;
                if (lead is >= 0xA1 and <= 0xF7 and not (0xA4 or 0xA5) && trail is >= 0xA1 and <= 0xFE)
                {
                    gb++;
                    if (lead is <= 0xA3 or (>= 0xB0 and <= 0xD7)) common++;
                }
                if (lead is >= 0xA1 and <= 0xF9 && (trail is >= 0x40 and <= 0x7E || trail is >= 0xA1 and <= 0xFE))
                {
                    big5++;
                    if (trail <= 0x7E) big5Low++;
                }
            }
            return new CjkStats(pairs, gb, common, big5, big5Low, invalid);
        }
    }
}
