using Avalonia.Controls;
using Avalonia.Controls.Documents;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.TaskLists;

namespace LightTextEditor.App.Views;

/// <summary>
/// 任务列表的选框（- [ ] / - [x]）。
/// 库自带的 TaskListNode 只显示、不能点；这里换成可以点击的选框，
/// 并在 <see cref="Control.Tag"/> 里记下“[ ]”在源码中的位置，供 <see cref="MarkdownPreview"/> 改写源码。
/// </summary>
public sealed class TaskListCheckBoxNode : InlineNode<TaskList>
{
    public const string CheckBoxClass = "TaskList";

    private readonly CheckBox _checkBox;

    public override Inline Inline { get; }

    public TaskListCheckBoxNode()
    {
        _checkBox = new CheckBox { Classes = { CheckBoxClass, "Interactive" } };
        Inline = new InlineUIContainer { Classes = { "TaskList" }, Child = _checkBox };
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        TaskList taskList,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        _checkBox.IsChecked = taskList.Checked;
        _checkBox.Tag = taskList.Span.Start;
        return true;
    }
}
