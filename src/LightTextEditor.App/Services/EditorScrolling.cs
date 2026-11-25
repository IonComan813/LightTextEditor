using Avalonia;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace LightTextEditor.App.Services;

internal static class EditorScrolling
{
    /// <summary>
    /// 把光标所在的行滚到可见处；不在屏幕上时滚到屏幕中间（查找、转到行用）。
    /// <para>
    /// AvaloniaEdit 自己的 <c>Caret.BringCaretToView</c> 在自动换行时经常滚错：还没显示过的行它按一行的高度估算，
    /// 实际折成几行，滚过去之后这些行量出了真实高度，目标行就被挤到了屏幕下面（往下跳时尤其明显，
    /// 往上跳经过的多是显示过的行，所以较少出错）。这里滚动之后立即重新布局，按量出的位置再校正，直到稳定。
    /// </para>
    /// </summary>
    public static void ScrollCaretIntoView(TextEditor editor)
    {
        var area = editor.TextArea;
        var view = area.TextView;
        for (int pass = 0; pass < 6; pass++)
        {
            editor.UpdateLayout(); // 按当前滚动位置排版，量出屏幕上各行的真实高度
            double height = view.Bounds.Height;
            if (height <= 0) break;
            var position = area.Caret.Position;
            double top = view.GetVisualPosition(position, VisualYPosition.LineTop).Y;
            double bottom = view.GetVisualPosition(position, VisualYPosition.LineBottom).Y;
            double offset = view.ScrollOffset.Y;
            if (top >= offset && bottom <= offset + height) break;
            double target = Math.Max(0, (top + bottom - height) / 2);
            if (Math.Abs(target - offset) < 0.5) break;
            // 直接设置 TextView 的滚动位置（与 Caret.BringCaretToView 相同）：
            // 经过 ScrollViewer 设置要等下一次布局才生效，这里量不到
            var scrollable = (ILogicalScrollable)view;
            scrollable.Offset = new Vector(view.ScrollOffset.X, target);
            scrollable.RaiseScrollInvalidated(EventArgs.Empty);
        }
        area.Caret.BringCaretToView(); // 横向（不换行时）
    }
}
