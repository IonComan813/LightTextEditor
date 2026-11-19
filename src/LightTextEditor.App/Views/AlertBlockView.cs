using Avalonia.Controls;
using Avalonia.Layout;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.Alerts;

namespace LightTextEditor.App.Views;

/// <summary>
/// GitHub 风格的提示框（> [!NOTE] 等）。
/// 库自带的 AlertBlockNode 只有一个 StackPanel，看不出是提示框，也不区分类型；
/// 这里包一层带左侧色条的边框，加上标题，并按类型加样式类（见 Styles/AppStyles.axaml）。
/// </summary>
public sealed class StyledAlertBlockNode : ContainerBlockNode<AlertBlock>
{
    private static readonly (string Kind, string Class, string Title)[] Kinds =
    [
        ("NOTE", "Note", "注意"),
        ("TIP", "Tip", "提示"),
        ("IMPORTANT", "Important", "重要"),
        ("WARNING", "Warning", "警告"),
        ("CAUTION", "Caution", "小心"),
    ];

    private readonly Border _border;
    private readonly TextBlock _title;

    public StyledAlertBlockNode()
    {
        _title = new TextBlock { Classes = { "AlertTitle" } };
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(_title);
        panel.Children.Add(container);
        _border = new Border { Classes = { "AlertBlock" }, Child = panel };
    }

    public override Control Control => _border;

    protected override bool UpdateCore(DocumentNode documentNode, AlertBlock block, in ObservableStringBuilderChangedEventArgs change, CancellationToken cancellationToken)
    {
        var result = base.UpdateCore(documentNode, block, change, cancellationToken);
        var kind = block.Kind.ToString().ToUpperInvariant();
        string title = kind;
        foreach (var (k, cls, t) in Kinds)
        {
            bool match = k == kind;
            _border.Classes.Set(cls, match);
            if (match) title = t;
        }
        _title.Text = title;
        return result;
    }
}
