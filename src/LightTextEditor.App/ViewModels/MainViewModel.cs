using CommunityToolkit.Mvvm.ComponentModel;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.ViewModels;

/// <summary>主窗口状态：当前文档、状态栏信息、视图选项。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(EncodingText), nameof(LineEndingText), nameof(FileTypeText), nameof(IsMarkdown), nameof(IsHtml))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty] private int _caretLine = 1;
    [ObservableProperty] private int _caretColumn = 1;
    [ObservableProperty] private int _selectionLength;
    [ObservableProperty] private int _characterCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1.0;

    /// <summary>当前标签是否显示 Markdown 预览。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewButtonText))]
    private bool _isPreviewing;

    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _centeredLayout;
    [ObservableProperty] private bool _showLineNumbers;
    [ObservableProperty] private bool _showStatusBar;

    public string WindowTitle => ActiveDocument is null
        ? "LightTextEditor"
        : $"{(ActiveDocument.IsDirty ? "● " : "")}{ActiveDocument.Title} - LightTextEditor";

    public string PositionText => SelectionLength > 0
        ? $"行 {CaretLine}，列 {CaretColumn}（已选 {SelectionLength} 个字符）"
        : $"行 {CaretLine}，列 {CaretColumn}";

    public string CharacterCountText => $"{CharacterCount:N0} 个字符";

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public string EncodingText => ActiveDocument is null
        ? ""
        : ActiveDocument.Encoding.DisplayName + (ActiveDocument.Encoding.IsCertain ? "" : "（未确认）");

    public string LineEndingText => ActiveDocument?.LineEnding.DisplayName() ?? "";

    public bool IsMarkdown => ActiveDocument is not null &&
        LightTextEditor.Core.Markdown.MarkdownAssets.IsMarkdownExtension(ActiveDocument.Extension);

    /// <summary>当前文件是网页（可以在浏览器中打开）。</summary>
    public bool IsHtml => ActiveDocument?.Extension.ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".svg";

    public string PreviewButtonText => IsPreviewing ? "返回编辑" : "预览";

    public string FileTypeText => ActiveDocument is null ? "" : LightTextEditor.Core.Library.FileTypes.DisplayName(ActiveDocument.Extension);

    partial void OnCaretLineChanged(int value) => OnPropertyChanged(nameof(PositionText));
    partial void OnCaretColumnChanged(int value) => OnPropertyChanged(nameof(PositionText));
    partial void OnSelectionLengthChanged(int value) => OnPropertyChanged(nameof(PositionText));
    partial void OnCharacterCountChanged(int value) => OnPropertyChanged(nameof(CharacterCountText));

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnDocumentPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnDocumentPropertyChanged;
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Title):
            case nameof(DocumentViewModel.IsDirty):
                OnPropertyChanged(nameof(WindowTitle));
                break;
            case nameof(DocumentViewModel.Encoding):
                OnPropertyChanged(nameof(EncodingText));
                break;
            case nameof(DocumentViewModel.LineEnding):
                OnPropertyChanged(nameof(LineEndingText));
                break;
            case nameof(DocumentViewModel.Extension):
                OnPropertyChanged(nameof(FileTypeText));
                OnPropertyChanged(nameof(IsMarkdown));
                OnPropertyChanged(nameof(IsHtml));
                break;
        }
    }
}
