using CommunityToolkit.Mvvm.ComponentModel;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.ViewModels;

/// <summary>主窗口状态：当前文档、状态栏信息、视图选项。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(EncodingText), nameof(LineEndingText))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty] private int _caretLine = 1;
    [ObservableProperty] private int _caretColumn = 1;
    [ObservableProperty] private int _selectionLength;
    [ObservableProperty] private int _characterCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1.0;

    [ObservableProperty] private bool _wordWrap;
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
        }
    }
}
