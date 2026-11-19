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

    /// <summary>未命名、未修改、空白的文档。打开文件时可直接替换掉它。</summary>
    public bool IsPristine => IsUntitled && !IsDirty && Document.TextLength == 0;

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
        _hasNonUndoableChanges = false;
        Document.UndoStack.MarkAsOriginalFile();
        RefreshDirty();
        Notice = null;
    }

    /// <summary>从磁盘重新读取（可指定编码）。</summary>
    public void Reload(TextEncoding? forcedEncoding = null)
    {
        if (FilePath is null) return;
        string text;
        TextEncoding enc;
        LineEnding ending;
        if (forcedEncoding is null)
        {
            var loaded = TextFileIO.Load(FilePath, LineEnding);
            (text, enc, ending) = (loaded.Text, loaded.Encoding, loaded.LineEnding);
        }
        else
        {
            var bytes = File.ReadAllBytes(FilePath);
            var bom = forcedEncoding.BomBytes;
            bool hasBom = bom.Length > 0 && bytes.AsSpan().StartsWith(bom);
            int skip = hasBom ? bom.Length : 0;
            text = forcedEncoding.Encoding.GetString(bytes, skip, bytes.Length - skip);
            enc = forcedEncoding with { HasBom = hasBom, IsCertain = true };
            ending = LineEndings.Detect(text, LineEnding, out _);
        }

        // 用 Replace 而不是赋值 Text，保留撤销历史（可以撤销"重新加载"）。
        Document.Replace(0, Document.TextLength, text);
        Encoding = enc;
        LineEnding = ending;
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

public sealed record NoticeAction(string Label, Action Execute);
