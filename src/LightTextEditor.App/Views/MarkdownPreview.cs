using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;

namespace LightTextEditor.App.Views;

/// <summary>Markdown 渲染预览（只读）。</summary>
internal sealed class MarkdownPreview : ScrollViewer
{
    private readonly MarkdownRenderer _renderer;
    private readonly ObservableStringBuilder _markdown = new();

    /// <summary>点击了链接（参数：链接地址）。</summary>
    public event Action<Uri>? LinkClicked;

    public MarkdownPreview()
    {
        _renderer = new MarkdownRenderer
        {
            MarkdownBuilder = _markdown,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(32, 20, 32, 40),
        };
        _renderer.LinkClick += (_, e) =>
        {
            e.Handled = true;
            if (e.HRef is { } uri) LinkClicked?.Invoke(uri);
        };

        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Background = Brushes.Transparent;
        Focusable = true;
        Content = _renderer;
    }

    /// <summary>显示 Markdown 文本。<paramref name="baseDirectory"/> 用于解析相对路径的图片。</summary>
    public void SetMarkdown(string text, string baseDirectory)
    {
        _renderer.ImageBasePath = baseDirectory;
        _markdown.Clear();
        _markdown.Append(text);
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
    public void ScrollToRatio(double ratio)
    {
        void Apply()
        {
            double range = Extent.Height - Viewport.Height;
            Offset = new Vector(0, range <= 0 ? 0 : Math.Clamp(ratio, 0, 1) * range);
        }
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(Apply, TimeSpan.FromMilliseconds(150));
    }
}
