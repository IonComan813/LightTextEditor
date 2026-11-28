using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace LightTextEditor.App.Views;

/// <summary>Markdown 渲染预览（只读；任务列表的选框可以点击）。</summary>
internal sealed class MarkdownPreview : ScrollViewer
{
    // 子类默认拿不到 ScrollViewer 的控件模板（没有模板就不能滚动），沿用 ScrollViewer 的样式
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    private readonly MarkdownRenderer _renderer;
    private readonly LayoutTransformControl _zoomHost;
    private readonly ObservableStringBuilder _markdown = new();
    private string? _text;
    private double _zoom = 1;

    /// <summary>点击了链接（参数：链接地址）。</summary>
    public event Action<Uri>? LinkClicked;

    /// <summary>
    /// 点击了任务列表的选框（参数：“[ ]”在源码中的位置、新的勾选状态）。
    /// 处理程序返回 false 时（源码已对不上）选框恢复原状。
    /// </summary>
    public event Func<int, bool, bool>? TaskToggled;

    static MarkdownPreview()
    {
        // 代码块不自动换行且代码超出宽度时（会出现横向滚动条）加上 hscroll 类，
        // 样式据此在代码下方给滚动条留出位置（见 Styles/MarkdownStyles.axaml）；放得下时不留空
        ExtentProperty.Changed.AddClassHandler<ScrollViewer>((sv, _) => UpdateCodeBlockScrollRoom(sv));
        ViewportProperty.Changed.AddClassHandler<ScrollViewer>((sv, _) => UpdateCodeBlockScrollRoom(sv));
    }

    private static void UpdateCodeBlockScrollRoom(ScrollViewer scrollViewer)
    {
        if (scrollViewer.Name != "PART_ScrollViewer" || scrollViewer.TemplatedParent is not CodeBlock block) return;
        bool overflow = !block.IsCodeWrapped && scrollViewer.Extent.Width > scrollViewer.Viewport.Width + 0.5;
        block.Classes.Set("hscroll", overflow);
    }

    public MarkdownPreview()
    {
        App.EnsureMarkdownResources();
        _renderer = new MarkdownRenderer
        {
            MarkdownBuilder = _markdown,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 左右留白 = A4 窄页边距（见 MainWindow.PageMargin），页面上的正文宽度与以后导出、打印时一致
            Margin = new Thickness(MainWindow.PageMargin, 20, MainWindow.PageMargin, 40),
        };
        _renderer.LinkClick += (_, e) =>
        {
            e.Handled = true;
            if (e.HRef is { } uri) LinkClicked?.Invoke(uri);
        };
        _renderer.AddHandler(Button.ClickEvent, OnButtonClick);

        // 缩放：整体按比例放大（文字、图片、代码块一起），相当于浏览器的缩放
        _zoomHost = new LayoutTransformControl { Child = _renderer, HorizontalAlignment = HorizontalAlignment.Stretch };

        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Brushes.Transparent;
        Focusable = true;
        Content = _zoomHost;
    }

    /// <summary>
    /// 显示 Markdown 文本。<paramref name="baseDirectory"/> 用于解析相对路径的图片。
    /// 返回是否重新渲染了（内容和图片目录都没变时不渲染，切换标签时保持原样）。
    /// </summary>
    public bool SetMarkdown(string text, string baseDirectory)
    {
        bool sameBase = _renderer.ImageBasePath == baseDirectory;
        _renderer.ImageBasePath = baseDirectory;
        if (sameBase && text == _text) return false;
        _text = text;
        _markdown.Clear();
        _markdown.Append(text);
        return true;
    }

    /// <summary>源码被预览自身改动（勾选任务）后调用：显示的内容已经一致，下次不用重新渲染。</summary>
    public void AcceptSourceText(string text) => _text = text;

    /// <summary>缩放比例（1 = 100%）。设置时保持视口中间的内容不动。</summary>
    public double Zoom
    {
        get => _zoom;
        set => SetZoom(value, null);
    }

    /// <summary>
    /// 缩放，保持 <paramref name="anchor"/>（视口内的位置，通常是鼠标所在处；为 null 时取视口中间）下的内容不动。
    /// 同步完成布局和滚动定位：先布局、下一帧再滚动会让画面跳一下（闪烁）。
    /// </summary>
    public void SetZoom(double zoom, Point? anchor)
    {
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        double anchorY = Math.Clamp(anchor?.Y ?? Viewport.Height / 2, 0, Math.Max(0, Viewport.Height));
        double contentY = (Offset.Y + anchorY) / _zoom; // 缩放前的内容坐标
        _zoom = zoom;
        _zoomHost.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? null : new ScaleTransform(zoom, zoom);
        UpdateLayout();
        double range = Math.Max(0, Extent.Height - Viewport.Height);
        Offset = new Vector(0, Math.Clamp(contentY * zoom - anchorY, 0, range));
    }

    /// <summary>
    /// 双击事件是否来自预览里的按钮、选框、滚动条等控件（如代码块的“换行”“复制”按钮）。
    /// 快速连点这些按钮时也会产生双击，不能当作“回到编辑”。
    /// </summary>
    public bool IsFromInteractiveElement(object? source)
    {
        for (var v = source as Visual; v is not null && v != this; v = v.GetVisualParent())
        {
            if (v is Button or ScrollBar or TextBox or Thumb) return true; // Button 包括 ToggleButton、CheckBox
            if (v.GetType().Name is "MermaidPresenter" or "PanAndZoom") return true; // Mermaid 图表可以拖动、缩放
        }
        return false;
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Name: "PART_CopyButton" } copy)
        {
            ShowCopied(copy);
            return;
        }
        if (e.Source is not CheckBox { Tag: int offset } box || !box.Classes.Contains(TaskListCheckBoxNode.CheckBoxClass))
            return;
        e.Handled = true;
        bool isChecked = box.IsChecked == true;
        if (TaskToggled?.Invoke(offset, isChecked) != true)
            box.IsChecked = !isChecked;
    }

    /// <summary>
    /// 代码块的“复制”按钮点击后：图标换成绿色对勾，并提示“已复制”，1.5 秒后恢复。
    /// （库自带的按钮复制后没有任何反馈。）
    /// </summary>
    private static void ShowCopied(Button button)
    {
        if (button.Tag is "copied") return;
        var original = button.Content;
        button.Tag = "copied";
        button.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M2,8.5 L6,12.5 L14,3.5"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xA3, 0x5A)),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, "已复制");
        ToolTip.SetIsOpen(button, true);
        DispatcherTimer.RunOnce(() =>
        {
            button.Content = original;
            button.Tag = null;
            ToolTip.SetIsOpen(button, false);
            button.ClearValue(ToolTip.TipProperty); // 恢复样式里的“复制代码”
        }, TimeSpan.FromMilliseconds(1500));
    }

    /// <summary>当前滚动位置占可滚动范围的比例（0~1）。</summary>
    public double ScrollRatio
    {
        get
        {
            double range = Extent.Height - Viewport.Height;
            return range <= 0 ? 0 : Offset.Y / range;
        }
    }

    /// <summary>按比例滚动。内容渲染是异步的，渲染完成后再定位一次。</summary>
    public void ScrollToRatio(double ratio, bool once = false)
    {
        void Apply()
        {
            double range = Extent.Height - Viewport.Height;
            Offset = new Vector(0, range <= 0 ? 0 : Math.Clamp(ratio, 0, 1) * range);
        }
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
        if (!once) DispatcherTimer.RunOnce(Apply, TimeSpan.FromMilliseconds(150));
    }
}
