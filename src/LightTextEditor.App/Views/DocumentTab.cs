using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.ViewModels;

namespace LightTextEditor.App.Views;

/// <summary>
/// 一个标签页：文档 + 编辑器。它是 TabView 的数据项；
/// 标签控件（FATabViewItem）由 TabView 生成，标签头由 <see cref="TabHeaderView"/> 模板现建。
/// （直接把 FATabViewItem 当作列表项时，移动标签会让它从界面上脱落。）
/// </summary>
internal sealed class DocumentTab(DocumentViewModel doc, TextEditor editor)
{
    public DocumentViewModel Doc { get; } = doc;
    public TextEditor Editor { get; } = editor;

    /// <summary>语法高亮（TextMate）。</summary>
    public Services.EditorHighlighter? Highlighting { get; set; }

    /// <summary>Markdown 预览（第一次切换到预览时创建）。</summary>
    public MarkdownPreview? Preview { get; set; }

    /// <summary>当前是否显示 Markdown 预览（否则显示源码编辑器）。</summary>
    public bool IsPreviewing { get; set; }

    /// <summary>当前显示这个标签的标签头（标签生成后才有）。</summary>
    public TabHeaderView? Header { get; internal set; }

    public Button CloseButton => Header?.CloseButton ?? throw new InvalidOperationException("标签尚未生成");

    public FATabViewItem TabItem =>
        Header?.FindAncestorOfType<FATabViewItem>() ?? throw new InvalidOperationException("标签尚未生成");
}

/// <summary>标签头：标题 + 关闭按钮（未保存时为 ●，见 Styles/AppStyles.axaml）。</summary>
internal sealed class TabHeaderView : StackPanel
{
    private readonly DocumentTab _tab;
    private readonly TextBlock _title;

    public Button CloseButton { get; }

    public TabHeaderView(DocumentTab tab, Action<DocumentTab> close)
    {
        _tab = tab;
        Orientation = Orientation.Horizontal;
        Background = Brushes.Transparent;

        _title = new TextBlock
        {
            Classes = { "tabTitle" },
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var glyphs = new Panel();
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "closeGlyph" } });
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Classes = { "dirtyDot" } });
        CloseButton = new Button { Content = glyphs, Classes = { "tabClose" } };
        ToolTip.SetTip(CloseButton, "关闭 (Ctrl+W)");
        CloseButton.Click += (_, e) =>
        {
            e.Handled = true;
            close(_tab);
        };

        Children.Add(_title);
        Children.Add(CloseButton);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // TabView 会用同一个模板在它自己的内容区再显示一次选中项；那一份不需要（编辑器在别处）
        if (this.FindAncestorOfType<FATabViewItem>() is null)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        _tab.Header = this;
        _tab.Doc.PropertyChanged += OnDocPropertyChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tab.Doc.PropertyChanged -= OnDocPropertyChanged; // 未订阅时也可以安全移除
        if (_tab.Header == this) _tab.Header = null;
    }

    private void OnDocPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.Title) or nameof(DocumentViewModel.IsDirty)
            or nameof(DocumentViewModel.ToolTip))
            Refresh();
    }

    private void Refresh()
    {
        _title.Text = _tab.Doc.Title;
        CloseButton.Classes.Set("dirty", _tab.Doc.IsDirty);
        ToolTip.SetTip(_title, _tab.Doc.ToolTip);
    }
}
