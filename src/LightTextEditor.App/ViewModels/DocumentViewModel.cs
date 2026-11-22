using System.IO.Hashing;
using System.Runtime.InteropServices;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.ViewModels;

/// <summary>一个打开的文档（一个标签页）。</summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    private const string UntitledTitle = "无标题";

    /// <summary>撤销栈之外的修改（如更换编码、换行符）也算"未保存"。</summary>
    private bool _hasNonUndoableChanges;

    /// <summary>上次打开 / 保存 / 重新加载时文本内容的指纹，用于判断文本是否真的改过。</summary>
    private TextFingerprint _savedText;

    public TextDocument Document { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ToolTip), nameof(IsUntitled), nameof(Extension))]
    private string? _filePath;

    [ObservableProperty]
    private TextEncoding _encoding;

    /// <summary>
    /// 打开 / 重新加载时的编码检测结果（保存后为 null）。
    /// <see cref="EncodingDetection.IsLossless"/> 为 false 时，文件中有无法按当前编码识别的字节。
    /// </summary>
    public EncodingDetection? Detection { get; private set; }

    /// <summary>文件中有无法按打开时的编码识别的字节（显示为 �），保存会改变它们。</summary>
    public bool HasUndecodableBytes => Detection is { IsLossless: false };

    private readonly List<int> _triedCodePages = [];

    /// <summary>
    /// 这次打开以来试过的编码（代码页，按先后顺序）：打开时检测考虑过的、打开时采用的、之后用户换过的。
    /// "更换编码重试"列表把它们显示为灰色。
    /// </summary>
    public IReadOnlyList<int> TriedCodePages => _triedCodePages;

    /// <summary>用户是否已经用"更换编码重试"换过编码（之后下拉按钮显示当前编码名，不再显示"更换编码重试"）。</summary>
    public bool HasSwitchedEncoding { get; private set; }

    private void MarkTried(IEnumerable<int> codePages)
    {
        foreach (var cp in codePages)
            if (!_triedCodePages.Contains(cp)) _triedCodePages.Add(cp);
    }

    /// <summary>
    /// 常驻提示：<see cref="Notice"/> 被清空（其他提示关闭、保存后等）时，用它的返回值补上。
    /// 用于"请确认当前文件的编码格式"，直到用户点 ✓ 或用关闭按钮关掉它（<see cref="DismissNotice"/>）为止。
    /// </summary>
    public Func<DocumentNotice?>? StandingNotice { get; set; }

    /// <summary>用户用关闭按钮关掉了常驻提示：不再自动补上（直到再次更换编码重试）。</summary>
    public bool IsStandingNoticeDismissed { get; set; }

    partial void OnNoticeChanged(DocumentNotice? value)
    {
        if (value is null && !IsStandingNoticeDismissed && StandingNotice?.Invoke() is { } standing) Notice = standing;
    }

    /// <summary>用户点了提示条的关闭按钮。关掉的是常驻提示时，之后不再自动显示它。</summary>
    public void DismissNotice()
    {
        if (Notice is { IsStanding: true }) IsStandingNoticeDismissed = true;
        Notice = null;
    }

    [ObservableProperty]
    private LineEnding _lineEnding;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _title = UntitledTitle;

    /// <summary>编辑区上方提示条（外部修改、编码不确定等）。</summary>
    [ObservableProperty]
    private DocumentNotice? _notice;

    /// <summary>上次读取/保存时文件的修改时间，用于发现外部修改。</summary>
    public DateTime? KnownWriteTimeUtc { get; set; }

    /// <summary>新建文档使用的扩展名（文件保存前有效）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Extension))]
    private string _newFileExtension;

    public bool IsUntitled => FilePath is null;

    public string Extension => FilePath is null ? NewFileExtension : Path.GetExtension(FilePath);

    public string ToolTip => FilePath ?? "尚未保存 — 按 Ctrl+S 直接保存到笔记库";


    /// <summary>关闭时恢复光标位置用。</summary>
    public int CaretOffset { get; set; }

    private DocumentViewModel(string text, string? filePath, TextEncoding encoding, LineEnding lineEnding, string newFileExtension)
    {
        Document = new TextDocument(text);
        Document.UndoStack.ClearAll();
        MarkTextAsSaved();
        _filePath = filePath;
        _encoding = encoding;
        _lineEnding = lineEnding;
        _newFileExtension = newFileExtension;

        Document.TextChanged += (_, _) => RefreshDirty(); // 在一组改动（如替换全部）结束后才触发一次
        UpdateTitle();
    }

    public static DocumentViewModel CreateNew(string extension, TextEncoding encoding, LineEnding lineEnding) =>
        new("", null, encoding, lineEnding, extension);

    public static DocumentViewModel FromFile(string path, LoadedText loaded)
    {
        var doc = new DocumentViewModel(loaded.Text, Path.GetFullPath(path), loaded.Encoding, loaded.LineEnding, Path.GetExtension(path));
        doc.KnownWriteTimeUtc = TryGetWriteTime(path);
        doc.Detection = loaded.Detection;
        doc.MarkTried(loaded.Detection.Candidates);
        doc.MarkTried([loaded.Encoding.CodePage]);
        return doc;
    }

    partial void OnFilePathChanged(string? value) => UpdateTitle();

    /// <summary>
    /// 是否未保存：编码、换行符等文本以外的修改，或文本与上次保存时不同。
    /// 文本按内容比较，不看撤销记录：打了几个字又删掉，内容和保存时一样，就不算未保存。
    /// </summary>
    private void RefreshDirty() => IsDirty = _hasNonUndoableChanges || IsTextChanged();

    private bool IsTextChanged()
    {
        // 不能用撤销栈的 IsOriginalFile 偷懒：TextChanged 触发时撤销栈还没更新
        if (Document.TextLength != _savedText.Length) return true; // 长度不同：一定改过（打字时通常在这里返回）
        return TextFingerprint.Of(Document) != _savedText;         // 长度相同才计算哈希
    }

    /// <summary>当前文本就是保存在磁盘上的内容。</summary>
    private void MarkTextAsSaved() => _savedText = TextFingerprint.Of(Document);

    /// <summary>
    /// 标签标题：文件名；未保存过的新文档一直是“无标题”，直到保存。
    /// （不再跟着第一行文字实时变化：标题变宽变窄时标签栏会跟着滚动。保存时的默认文件名仍取第一行文字，见 FileNaming.SuggestBaseName。）
    /// </summary>
    private void UpdateTitle() => Title = FilePath is null ? UntitledTitle : Path.GetFileName(FilePath);

    /// <summary>更改保存时使用的编码（不改变文本内容）。</summary>
    public void ChangeEncoding(TextEncoding encoding)
    {
        if (Encoding == encoding) return;
        Encoding = encoding;
        _hasNonUndoableChanges = true;
        RefreshDirty();
    }

    /// <summary>把文本中的换行符统一为指定类型。</summary>
    public void ChangeLineEnding(LineEnding ending)
    {
        var normalized = LineEndings.Normalize(Document.Text, ending);
        if (normalized != Document.Text)
            Document.Replace(0, Document.TextLength, normalized); // 可撤销
        if (LineEnding != ending)
        {
            LineEnding = ending;
            _hasNonUndoableChanges = true;
        }
        RefreshDirty();
    }

    /// <summary>
    /// 写入磁盘。<paramref name="path"/> 为空时写回当前文件。
    /// 编码无法表示文本时抛出 <see cref="EncodingLossException"/>，此时不会写入任何内容。
    /// </summary>
    public void SaveTo(string path, TextEncoding? encoding = null)
    {
        var enc = encoding ?? Encoding;
        TextFileIO.Save(path, Document.Text, enc);

        FilePath = Path.GetFullPath(path);
        // 按原来"待确认"的编码保存时仍是待确认（要用户点 ✓）；保存为用户明确选择的编码时为确定
        Encoding = enc;
        KnownWriteTimeUtc = TryGetWriteTime(path);
        // 写入的字节就是按此编码编码的，不再有无法识别的字节
        Detection = Detection is null ? null : Detection with { IsLossless = true };
        _hasNonUndoableChanges = false;
        MarkTextAsSaved();
        RefreshDirty();
        Notice = null;
    }

    /// <summary>
    /// 从磁盘重新读取。<paramref name="forcedEncoding"/> 不为空时按该编码解码（"以其他编码重新打开"，
    /// 结果为"待确认"），否则重新检测编码（<paramref name="remembered"/> 为编码记录中的编码）。
    /// </summary>
    public void Reload(TextEncoding? forcedEncoding = null, TextEncoding? remembered = null)
    {
        if (FilePath is null) return;
        var loaded = forcedEncoding is null
            ? TextFileIO.Load(FilePath, LineEnding, remembered)
            : TextFileIO.DecodeAs(File.ReadAllBytes(FilePath), forcedEncoding, LineEnding, isCertain: false);
        MarkTried(loaded.Detection.Candidates);
        MarkTried([loaded.Encoding.CodePage]);
        if (forcedEncoding is not null) HasSwitchedEncoding = true;

        // 用 Replace 而不是赋值 Text，保留撤销历史（可以撤销"重新加载"）。
        Document.Replace(0, Document.TextLength, loaded.Text);
        Detection = loaded.Detection;
        Encoding = loaded.Encoding;
        LineEnding = loaded.LineEnding;
        KnownWriteTimeUtc = TryGetWriteTime(FilePath);
        _hasNonUndoableChanges = false;
        MarkTextAsSaved();
        RefreshDirty();
    }

    /// <summary>文件被重命名或移动后更新路径。</summary>
    public void UpdatePath(string newPath)
    {
        FilePath = Path.GetFullPath(newPath);
        KnownWriteTimeUtc = TryGetWriteTime(newPath);
    }

    /// <summary>磁盘上的文件不见了：标记为未保存，按 Ctrl+S 可重新写回。</summary>
    public void MarkMissingOnDisk()
    {
        KnownWriteTimeUtc = null;
        _hasNonUndoableChanges = true;
        RefreshDirty();
    }

    public static DateTime? TryGetWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// 文本内容的指纹：长度 + XxHash3（64 位，非加密哈希，速度接近内存带宽）。
/// 分块读取文档，不把整篇文本复制成一个字符串。
/// </summary>
internal readonly record struct TextFingerprint(int Length, ulong Hash)
{
    public static TextFingerprint Of(TextDocument document)
    {
        var hash = new XxHash3();
        var buffer = new char[16 * 1024];
        using var reader = document.CreateReader();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            hash.Append(MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));
        return new TextFingerprint(document.TextLength, hash.GetCurrentHashAsUInt64());
    }
}

public enum NoticeSeverity { Informational, Success, Warning, Error }

/// <summary>编辑区上方的提示条。</summary>
public sealed record DocumentNotice(string Title, string Message, NoticeSeverity Severity, IReadOnlyList<NoticeAction> Actions)
{
    /// <summary>常驻提示（见 <see cref="DocumentViewModel.StandingNotice"/>）：被其他提示替换后会回来，用关闭按钮关掉则不再出现。</summary>
    public bool IsStanding { get; init; }
}

public enum NoticeIcon { None, Checkmark }

/// <summary>
/// 提示条上的按钮。有 <see cref="Children"/> 时显示为下拉按钮（<see cref="Execute"/> 不使用）；
/// 有 <see cref="Icon"/> 时只显示图标，<see cref="Label"/> 作为提示文字。
/// </summary>
public sealed record NoticeAction(string Label, Action Execute)
{
    public IReadOnlyList<NoticeAction>? Children { get; init; }

    public NoticeIcon Icon { get; init; }

    /// <summary>作为下拉菜单项时：是否打勾（当前项）。</summary>
    public bool IsChecked { get; init; }

    /// <summary>作为下拉菜单项时：文字显示为灰色（仍可选择），如已经试过的编码。</summary>
    public bool IsDimmed { get; init; }
}
