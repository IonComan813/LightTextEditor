using Avalonia.Controls;

using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.ViewModels;

namespace LightTextEditor.App.Views;

/// <summary>一个标签页：文档 + 标签 + 编辑器。</summary>
internal sealed class DocumentTab
{
    public DocumentViewModel Doc { get; }
    public FATabViewItem TabItem { get; }
    public TextEditor Editor { get; }
    public Button CloseButton { get; }

    private readonly TextBlock _titleText;

    public DocumentTab(DocumentViewModel doc, TextEditor editor)
    {
        Doc = doc;
        Editor = editor;

        _titleText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 关闭按钮：未保存时显示 ●，鼠标移上去变成 ×（样式见 Styles/AppStyles.axaml）
        var glyphs = new Panel();
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "closeGlyph" } });
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Classes = { "dirtyDot" } });
        CloseButton = new Button { Content = glyphs, Classes = { "tabClose" } };
        ToolTip.SetTip(CloseButton, "关闭 (Ctrl+W)");

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_titleText);
        header.Children.Add(CloseButton);

        TabItem = new FATabViewItem
        {
            Header = header,
            IsClosable = false, // 使用自己的关闭按钮
            Tag = this,
        };

        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DocumentViewModel.Title) or nameof(DocumentViewModel.IsDirty)
                or nameof(DocumentViewModel.ToolTip))
                Refresh();
        };
        Refresh();
    }

    private void Refresh()
    {
        _titleText.Text = Doc.Title;
        CloseButton.Classes.Set("dirty", Doc.IsDirty);
        ToolTip.SetTip(TabItem, Doc.ToolTip);
    }
}
