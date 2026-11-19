using System.Text;

namespace LightTextEditor.Core.Text;

/// <summary>从磁盘读取的文本及其格式信息。</summary>
public sealed record LoadedText(string Text, EncodingDetection Detection, LineEnding LineEnding, bool HasMixedLineEndings, bool LooksBinary)
{
    public TextEncoding Encoding => Detection.Encoding;
}

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
    /// <param name="remembered">编码记录中该文件已确认的编码（见 <see cref="EncodingMemory"/>）。</param>
    /// <param name="useEditorConfig">是否参考文件所在目录的 <c>.editorconfig</c>。</param>
    public static LoadedText Load(string path, LineEnding defaultLineEnding = LineEnding.CrLf,
        TextEncoding? remembered = null, bool useEditorConfig = true)
    {
        var bytes = File.ReadAllBytes(path);
        var charset = useEditorConfig ? EditorConfig.GetCharset(path) : null;
        return Decode(bytes, defaultLineEnding, remembered, charset);
    }

    /// <summary>识别编码（见 <see cref="EncodingDetector"/>）并解码。</summary>
    public static LoadedText Decode(byte[] bytes, LineEnding defaultLineEnding = LineEnding.CrLf,
        TextEncoding? remembered = null, string? editorConfigCharset = null)
    {
        var detection = EncodingDetector.Detect(bytes, remembered, editorConfigCharset);
        return Decode(bytes, detection, defaultLineEnding);
    }

    /// <summary>
    /// 按指定编码解码（"以其他编码重新打开"）。文件开头有该编码的 BOM 时跳过 BOM。
    /// 结果的 <see cref="TextEncoding.IsCertain"/> 由 <paramref name="isCertain"/> 决定（用户还在试编码时为 false）。
    /// </summary>
    public static LoadedText DecodeAs(byte[] bytes, TextEncoding encoding, LineEnding defaultLineEnding = LineEnding.CrLf, bool isCertain = true)
    {
        var bom = encoding.BomBytes;
        bool hasBom = bom.Length > 0 && bytes.AsSpan().StartsWith(bom);
        var enc = encoding with { HasBom = hasBom, IsCertain = isCertain };
        return Decode(bytes, new EncodingDetection(enc, DetectionMethod.Manual, 1, EncodingDetector.RoundTrips(bytes, enc)), defaultLineEnding);
    }

    private static LoadedText Decode(byte[] bytes, EncodingDetection detection, LineEnding defaultLineEnding)
    {
        var encoding = detection.Encoding;
        int skip = encoding.HasBom && bytes.AsSpan().StartsWith(encoding.BomBytes) ? encoding.BomBytes.Length : 0;

        // 解码时不抛异常：无法识别的字节显示为替换字符（是否有损见 Detection.IsLossless）
        var lenient = (Encoding)encoding.Encoding.Clone();
        lenient.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
        var text = lenient.GetString(bytes, skip, bytes.Length - skip);

        // UTF-16/32 本来就有大量 0x00，不算二进制
        bool looksBinary = (!encoding.IsUnicode || encoding.IsUtf8) && ContainsNul(bytes);
        var ending = LineEndings.Detect(text, defaultLineEnding, out bool mixed);
        return new LoadedText(text, detection, ending, mixed, looksBinary);
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
