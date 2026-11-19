using LightTextEditor.Core.Settings;

namespace LightTextEditor.Core.Text;

/// <summary>
/// 编码记录：用户确认过 / 选择过的文件编码（路径 → 代码页），下次打开时直接使用，不改动文件内容。
/// 只对"看内容猜不准"的编码有意义：带 BOM 的文件和合法 UTF-8 不需要记录。
/// 保存在配置目录的 <c>encodings.json</c>（M4 引入元数据库后迁移过去）。
/// </summary>
public sealed class EncodingMemory
{
    /// <summary>最多记录的文件数，超出时丢弃最久未用的。</summary>
    public const int Capacity = 2000;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string? _storePath;
    private readonly Dictionary<string, EncodingMemoryEntry> _entries = new(PathComparer);

    /// <param name="storePath">保存位置；为 null 时只在内存中（测试用）。</param>
    public EncodingMemory(string? storePath)
    {
        _storePath = storePath;
        if (storePath is not null)
        {
            foreach (var entry in JsonStore.LoadEncodingMemory(storePath).Entries)
                if (!string.IsNullOrEmpty(entry.Path)) _entries[entry.Path] = entry;
        }
    }

    public int Count => _entries.Count;

    /// <summary>该文件记录的编码；没有记录或代码页在本机不可用时返回 null。</summary>
    public TextEncoding? Get(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out var entry)) return null;
        try { return TextEncoding.FromCodePage(entry.CodePage); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// 记录文件的编码。带 BOM 的 Unicode 编码能自我识别，不记录（并清除旧记录）。
    /// </summary>
    public void Remember(string path, TextEncoding encoding)
    {
        var key = Normalize(path);
        if (encoding.HasBom || (encoding.IsUnicode && !encoding.IsUtf8))
        {
            Forget(key);
            return;
        }
        if (_entries.TryGetValue(key, out var existing) && existing.CodePage == encoding.CodePage)
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            Persist();
            return;
        }
        _entries[key] = new EncodingMemoryEntry { Path = key, CodePage = encoding.CodePage, LastUsedUtc = DateTime.UtcNow };
        Trim();
        Persist();
    }

    public void Forget(string path)
    {
        if (_entries.Remove(Normalize(path))) Persist();
    }

    /// <summary>文件被重命名或移动后，记录跟着走。</summary>
    public void Move(string oldPath, string newPath)
    {
        var oldKey = Normalize(oldPath);
        if (!_entries.Remove(oldKey, out var entry)) return;
        var newKey = Normalize(newPath);
        entry.Path = newKey;
        _entries[newKey] = entry;
        Persist();
    }

    private void Trim()
    {
        if (_entries.Count <= Capacity) return;
        foreach (var old in _entries.Values.OrderBy(e => e.LastUsedUtc).Take(_entries.Count - Capacity).ToList())
            _entries.Remove(old.Path);
    }

    private void Persist()
    {
        if (_storePath is null) return;
        try
        {
            JsonStore.SaveEncodingMemory(new EncodingMemoryData { Entries = [.. _entries.Values] }, _storePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 记录保存失败不影响使用
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

public sealed class EncodingMemoryData
{
    public List<EncodingMemoryEntry> Entries { get; set; } = [];
}

public sealed class EncodingMemoryEntry
{
    public string Path { get; set; } = "";
    public int CodePage { get; set; }
    public DateTime LastUsedUtc { get; set; }
}
