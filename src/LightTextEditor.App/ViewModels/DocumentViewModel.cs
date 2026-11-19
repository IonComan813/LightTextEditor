using System.ComponentModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using LightTextEditor.Core.Library;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.ViewModels;

/// <summary>一个打开的文档（一个标签页）。</summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    private const string UntitledTitle = "无标题";

    /// <summary>撤销栈之外的修改（如更换编码、换行符）也算"未保存"。</summary>
    private bool _hasNonUndoableChanges;

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

    /// <summary>这次打开以来用过的编码（代码页），"换一个编码"菜单里把它们显示为灰色。</summary>
    public HashSet<int> TriedCodePages { get; } = [];

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
        Document.UndoStack.MarkAsOriginalFile();
        _filePath = filePath;
        _encoding = encoding;
        _lineEnding = lineEnding;
        _newFileExtension = newFileExtension;

        Document.UndoStack.PropertyChanged += OnUndoStackPropertyChanged;
        Document.TextChanged += (_, _) => { if (IsUntitled) UpdateTitle(); };
        UpdateTitle();
    }

    public static DocumentViewModel CreateNew(string extension, TextEncoding encoding, LineEnding lineEnding) =>
        new("", null, encoding, lineEnding, extension);

    public static DocumentViewModel FromFile(string path, LoadedText loaded)
    {
        var doc = new DocumentViewModel(loaded.Text, Path.GetFullPath(path), loaded.Encoding, loaded.LineEnding, Path.GetExtension(path));
        doc.KnownWriteTimeUtc = TryGetWriteTime(path);
        doc.Detection = loaded.Detection;
        doc.TriedCodePages.Add(loaded.Encoding.CodePage);
        return doc;
    }

    partial void OnFilePathChanged(string? value) => UpdateTitle();

    private void OnUndoStackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) RefreshDirty();
    }

    private void RefreshDirty() => IsDirty = _hasNonUndoableChanges || !Document.UndoStack.IsOriginalFile;

    private void UpdateTitle()
    {
        Title = FilePath is not null
            ? Path.GetFileName(FilePath)
            // 只看开头部分：未命名文档每次按键都会刷新标题，不能每次复制整篇文本
            : FileNaming.FirstLineTitle(Document.GetText(0, Math.Min(Document.TextLength, 4096))) ?? UntitledTitle;
    }

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
        Encoding = enc with { IsCertain = true };
        KnownWriteTimeUtc = TryGetWriteTime(path);
        Detection = null;
        _hasNonUndoableChanges = false;
        Document.UndoStack.MarkAsOriginalFile();
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
        TriedCodePages.Add(loaded.Encoding.CodePage);

        // 用 Replace 而不是赋值 Text，保留撤销历史（可以撤销"重新加载"）。
        Document.Replace(0, Document.TextLength, loaded.Text);
        Detection = loaded.Detection;
        Encoding = loaded.Encoding;
        LineEnding = loaded.LineEnding;
        KnownWriteTimeUtc = TryGetWriteTime(FilePath);
        _hasNonUndoableChanges = false;
        Document.UndoStack.MarkAsOriginalFile();
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

public enum NoticeSeverity { Informational, Success, Warning, Error }

/// <summary>编辑区上方的提示条。</summary>
public sealed record DocumentNotice(string Title, string Message, NoticeSeverity Severity, IReadOnlyList<NoticeAction> Actions);

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
