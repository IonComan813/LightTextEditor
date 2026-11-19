using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace LightTextEditor.App.Views;

// 提示条可以拖动（避免挡住正文）：按住提示条上按钮以外的地方拖动；双击空白处回到左上角。
// 位置在本次运行中保留（之后的提示条也出现在那里），并始终限制在编辑区内。
public partial class MainWindow
{
    private readonly TranslateTransform _noticeOffset = new();
    private Point? _noticeDragStart;
    private Vector _noticeDragStartOffset;

    /// <summary>提示条相对默认位置（编辑区左上角）的偏移。</summary>
    internal Vector NoticeOffset => new(_noticeOffset.X, _noticeOffset.Y);

    private void InitializeNoticeDrag()
    {
        NoticeHost.RenderTransform = _noticeOffset;
        NoticeHost.Cursor = new Cursor(StandardCursorType.SizeAll);
        // 按钮已经处理了按下事件，所以这里只会收到按在空白处、标题、文字上的拖动
        NoticeHost.AddHandler(PointerPressedEvent, OnNoticePointerPressed, RoutingStrategies.Bubble);
        NoticeHost.AddHandler(PointerMovedEvent, OnNoticePointerMoved, RoutingStrategies.Bubble);
        NoticeHost.AddHandler(PointerReleasedEvent, OnNoticePointerReleased, RoutingStrategies.Bubble);
        NoticeHost.AddHandler(PointerCaptureLostEvent, (_, _) => _noticeDragStart = null, RoutingStrategies.Direct | RoutingStrategies.Bubble);
        // 窗口或提示条大小变化后，保证提示条仍在编辑区内
        EditorArea.SizeChanged += (_, _) => ClampNoticeOffset();
        NoticeHost.SizeChanged += (_, _) => ClampNoticeOffset();
    }

    private void OnNoticePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(NoticeHost).Properties.IsLeftButtonPressed || NoticeHost.Parent is not Visual parent) return;
        if (e.ClickCount == 2)
        {
            MoveNoticeTo(default);
            e.Handled = true;
            return;
        }
        _noticeDragStart = e.GetPosition(parent);
        _noticeDragStartOffset = NoticeOffset;
        e.Pointer.Capture(NoticeHost);
        e.Handled = true;
    }

    private void OnNoticePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_noticeDragStart is not { } start || NoticeHost.Parent is not Visual parent) return;
        MoveNoticeTo(_noticeDragStartOffset + (e.GetPosition(parent) - start));
        e.Handled = true;
    }

    private void OnNoticePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_noticeDragStart is null) return;
        _noticeDragStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void MoveNoticeTo(Vector offset)
    {
        var clamped = ClampToEditorArea(offset);
        _noticeOffset.X = clamped.X;
        _noticeOffset.Y = clamped.Y;
    }

    private void ClampNoticeOffset() => MoveNoticeTo(NoticeOffset);

    /// <summary>限制偏移量，使提示条整个留在编辑区内（提示条比编辑区还大时贴住左上角）。</summary>
    private Vector ClampToEditorArea(Vector offset)
    {
        var host = NoticeHost.Bounds;   // 不含 RenderTransform 的布局位置
        var area = EditorArea.Bounds;   // 与提示条在同一个 Grid 中
        if (host.Width <= 0 || area.Width <= 0) return offset;

        static double Clamp(double v, double min, double max) => max < min ? min : Math.Clamp(v, min, max);
        return new Vector(
            Clamp(offset.X, area.Left - host.Left, area.Right - host.Right),
            Clamp(offset.Y, area.Top - host.Top, area.Bottom - host.Bottom));
    }
}
