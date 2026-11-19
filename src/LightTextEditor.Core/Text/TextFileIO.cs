using System.Text;

namespace LightTextEditor.Core.Text;

/// <summary>从磁盘读取的文本及其格式信息。</summary>
public sealed record LoadedText(string Text, TextEncoding Encoding, LineEnding LineEnding, bool HasMixedLineEndings, bool LooksBinary);

/// <summary>保存时目标编码无法无损表示某个字符。</summary>
public sealed class EncodingLossException(TextEncoding encoding, char character, int index)
    : Exception($"编码 {encoding.DisplayName} 无法表示字符 '{character}'（位置 {index}）。")
{
    public TextEncoding TargetEncoding { get; } = encoding;
    public char Character { get; } = character;
    public int Index { get; } = index;
}

/// <summary>文本文件的读取（编码识别）与安全写入（原子替换）。</summary>
public static class TextFileIO
{
    /// <summary>读取文件并识别编码与换行符。</summary>
    public static LoadedText Load(string path, LineEnding defaultLineEnding = LineEnding.CrLf)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes, defaultLineEnding);
    }

    /// <summary>
    /// 识别顺序：BOM → 严格 UTF-8 校验 → 回退为 GB18030（标记为"不确定"）。
    /// （统计猜测将在 M3 加入。）
    /// </summary>
    public static LoadedText Decode(byte[] bytes, LineEnding defaultLineEnding = LineEnding.CrLf)
    {
        TextEncoding.EnsureCodePagesRegistered();

        var (encoding, bomLength) = DetectBom(bytes);
        string text;
        bool looksBinary;

        if (encoding is not null)
        {
            text = encoding.Encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            looksBinary = !encoding.IsUnicode && ContainsNul(bytes);
        }
        else
        {
            looksBinary = ContainsNul(bytes);
            var strictUtf8 = new UTF8Encoding(false, true);
            try
            {
                text = strictUtf8.GetString(bytes);
                encoding = TextEncoding.Utf8(withBom: false);
            }
            catch (DecoderFallbackException)
            {
                encoding = TextEncoding.Gb18030(isCertain: false);
                text = encoding.Encoding.GetString(bytes);
            }
        }

        var ending = LineEndings.Detect(text, defaultLineEnding, out bool mixed);
        return new LoadedText(text, encoding, ending, mixed, looksBinary);
    }

    private static (TextEncoding? Encoding, int BomLength) DetectBom(byte[] b)
    {
        // UTF-32 LE 必须在 UTF-16 LE 之前判断（前两个字节相同）。
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            return (new TextEncoding(new UTF32Encoding(false, false), true, true), 4);
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            return (new TextEncoding(new UTF32Encoding(true, false), true, true), 4);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (TextEncoding.Utf8(withBom: true), 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (new TextEncoding(new UnicodeEncoding(false, false), true, true), 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (new TextEncoding(new UnicodeEncoding(true, false), true, true), 2);
        return (null, 0);
    }

    private static bool ContainsNul(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 8192);
        return Array.IndexOf(bytes, (byte)0, 0, n) >= 0;
    }

    /// <summary>
    /// 把文本编码为字节（含 BOM）。目标编码无法无损表示文本时抛出 <see cref="EncodingLossException"/>。
    /// </summary>
    public static byte[] Encode(string text, TextEncoding encoding)
    {
        TextEncoding.EnsureCodePagesRegistered();

        var strict = (Encoding)encoding.Encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;

        byte[] body;
        try
        {
            body = strict.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new EncodingLossException(encoding, ex.CharUnknown != '\0' ? ex.CharUnknown : ex.CharUnknownHigh, ex.Index);
        }

        if (!encoding.HasBom) return body;
        var bom = encoding.BomBytes;
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    /// <summary>编码并以原子方式写入文件。</summary>
    public static void Save(string path, string text, TextEncoding encoding)
    {
        WriteAllBytesAtomic(path, Encode(text, encoding));
    }

    /// <summary>
    /// 先写入同目录下的临时文件并刷到磁盘，再替换目标文件。
    /// 写入过程中崩溃或断电时，原文件保持完整。
    /// </summary>
    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException($"文件是只读的：{fullPath}");

                try
                {
                    // File.Replace 在 Windows 上会保留原文件的属性、ACL 和创建时间。
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception e) when (e is IOException or PlatformNotSupportedException)
                {
                    // 部分网络共享 / 文件系统不支持 Replace，退回到覆盖式移动。
                    File.Move(tempPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 尽力清理 */ }
        }
    }
}
