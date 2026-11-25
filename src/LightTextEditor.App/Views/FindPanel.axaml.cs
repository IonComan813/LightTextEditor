using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.RegularExpressions;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using LightTextEditor.App.Services;

namespace LightTextEditor.App.Views;

/// <summary>
/// 查找 / 替换面板（代替 AvaloniaEdit 自带的 SearchPanel）。
/// <list type="bullet">
/// <item>浮在编辑区上、不透明，可以拖动；所有标签页共用一个，作用于当前标签页的编辑器（<see cref="Editor"/>）。</item>
/// <item>不在编辑器的可视树里：自带的面板在编辑器内部，输入法向输入框要客户端的请求会冒泡到编辑器，
/// 被换成编辑器的组字客户端（见 <see cref="EditorPreedit"/>），拼音就跑到了正文里。</item>
/// <item>所有匹配项用与选中文字相同的颜色标出；当前项就是编辑器里选中的文字。</item>
/// </list>
/// </summary>
public partial class FindPanel : UserControl
{
    /// <summary>最多记录这么多个匹配项（用于计数、标出）；超过时只标出屏幕上的，计数显示为“N+”。</summary>
    internal const int MaxMatches = 100_000;

    private TextEditor? _editor;
    private TextDocument? _document;
    private readonly MatchRenderer _renderer;
    private readonly DispatcherTimer _refreshTimer;
    private ISearchStrategy? _strategy;
    private TextSegmentCollection<TextSegment>? _matches;
    private bool _capped;
    private string? _error;
    private string? _transientStatus;

    private readonly TranslateTransform _offset = new();
    private Point? _dragStart;
    private Vector _dragStartOffset;

    /// <summary>用户放面板的位置（拖动、双击复位）。跳转时为避开匹配项临时挪开，之后不挡时回到这里。</summary>
    private Vector _home;

    public FindPanel()
    {
        InitializeComponent();
        _renderer = new MatchRenderer(this);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh(selectFirst: false);
        };

        IsVisible = false;
        RenderTransform = _offset;
        Frame.Cursor = new Cursor(StandardCursorType.SizeAll);

        // 不用 TextChanged：它在文字改变之后才异步引发
        FindBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(selectFirst: true); };
        MatchCaseToggle.IsCheckedChanged += (_, _) => Refresh(selectFirst: true);
        WholeWordToggle.IsCheckedChanged += (_, _) => Refresh(selectFirst: true);
        RegexToggle.IsCheckedChanged += (_, _) => Refresh(selectFirst: true);
        ReplaceToggle.Click += (_, _) => IsReplaceMode = !IsReplaceMode;
        PreviousButton.Click += (_, _) => FindPrevious();
        NextButton.Click += (_, _) => FindNext();
        CloseButton.Click += (_, _) => Close();
        ReplaceButton.Click += (_, _) => ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // 按钮、输入框已经处理了按下事件，所以这里只会收到按在空白处、文字上的拖动
        Frame.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        Frame.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        Frame.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
        Frame.AddHandler(PointerCaptureLostEvent, (_, _) => _dragStart = null, RoutingStrategies.Direct | RoutingStrategies.Bubble);
        SizeChanged += (_, _) => ClampOffset();
    }

    /// <summary>面板关闭时引发（窗口据此把焦点还给编辑器）。</summary>
    public event EventHandler? Closed;

    /// <summary>拖动时面板不能离开这个控件的范围（编辑区）；它与面板在同一个父容器中。</summary>
    public Control? DragBounds
    {
        get;
        set
        {
            if (field is not null) field.SizeChanged -= OnBoundsSizeChanged;
            field = value;
            if (field is not null) field.SizeChanged += OnBoundsSizeChanged;
        }
    }

    public bool IsOpen => IsVisible;

    public string SearchPattern
    {
        get => FindBox.Text ?? "";
        set => FindBox.Text = value;
    }

    public string ReplacePattern
    {
        get => ReplaceBox.Text ?? "";
        set => ReplaceBox.Text = value;
    }

    public bool IsReplaceMode
    {
        get => ReplaceRow.IsVisible;
        set
        {
            ReplaceRow.IsVisible = value;
            ReplaceToggleGlyph.Data = Geometry.Parse(value ? "M0,0 L4,4 L8,0" : "M0,0 L4,4 L0,8");
        }
    }

    public bool MatchCase
    {
        get => MatchCaseToggle.IsChecked == true;
        set => MatchCaseToggle.IsChecked = value;
    }

    public bool WholeWords
    {
        get => WholeWordToggle.IsChecked == true;
        set => WholeWordToggle.IsChecked = value;
    }

    public bool UseRegex
    {
        get => RegexToggle.IsChecked == true;
        set => RegexToggle.IsChecked = value;
    }

    /// <summary>状态文字（“3 / 12”“无结果”等；测试用）。</summary>
    internal string StatusMessage => StatusText.Text ?? "";

    /// <summary>当前记录的匹配项数（测试用）。</summary>
    internal int MatchCount => _matches?.Count ?? 0;

    /// <summary>面板相对默认位置（编辑区右上角）的偏移。</summary>
    internal Vector Offset => new(_offset.X, _offset.Y);

    internal TextBox FindTextBox => FindBox;
    internal TextBox ReplaceTextBox => ReplaceBox;

    /// <summary>查找作用于哪个编辑器（当前标签页的）。切换标签页时更换。</summary>
    public TextEditor? Editor
    {
        get => _editor;
        set
        {
            if (_editor == value) return;
            Detach();
            _editor = value;
            if (IsOpen) Attach();
        }
    }

    /// <summary>打开面板，焦点放到查找框并选中其中的文字（不用编辑器里选中的文字填充）。</summary>
    public void Open(bool replace)
    {
        IsReplaceMode = replace || (IsOpen && IsReplaceMode);
        if (!IsOpen)
        {
            IsVisible = true;
            Attach();
        }
        _transientStatus = null;
        UpdateStatus();
        var box = replace && !string.IsNullOrEmpty(SearchPattern) ? ReplaceBox : FindBox;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    public void Close()
    {
        if (!IsOpen) return;
        Detach();
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────── 查找 ─────────────────────────

    private void Attach()
    {
        if (_editor is null) return;
        _document = _editor.Document;
        _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _editor.TextArea.SelectionChanged += OnSelectionChanged;
        _editor.DocumentChanged += OnEditorDocumentChanged;
        _document.TextChanged += OnTextChanged;
        Refresh(selectFirst: false);
    }

    private void Detach()
    {
        _refreshTimer.Stop();
        if (_editor is not null)
        {
            _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            _editor.TextArea.SelectionChanged -= OnSelectionChanged;
            _editor.DocumentChanged -= OnEditorDocumentChanged;
            _editor.TextArea.TextView.InvalidateLayer(_renderer.Layer);
        }
        if (_document is not null) _document.TextChanged -= OnTextChanged;
        DisconnectMatches();
        _document = null;
    }

    private void OnEditorDocumentChanged(object? sender, EventArgs e)
    {
        var editor = _editor;
        Detach();
        _editor = editor;
        Attach();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnTextChanged(object? sender, EventArgs e)
    {
        // 匹配项的位置随文档自动调整；稍后重新查找（连续输入时只查一次）
        _transientStatus = null;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void DisconnectMatches()
    {
        if (_matches is not null && _document is not null) _matches.Disconnect(_document);
        _matches = null;
        _capped = false;
    }

    /// <summary>
    /// 按当前的查找内容和选项重新查找所有匹配项并标出。<paramref name="selectFirst"/>：
    /// 查找内容或选项变了，选中从当前位置起的第一个匹配项（边输入边查找）。
    /// </summary>
    private void Refresh(bool selectFirst)
    {
        _refreshTimer.Stop();
        DisconnectMatches();
        _strategy = null;
        _error = null;
        if (selectFirst) _transientStatus = null;

        var pattern = SearchPattern;
        if (_editor is not null && _document is not null && pattern.Length > 0)
        {
            try
            {
                _strategy = SearchStrategyFactory.Create(pattern, !MatchCase, WholeWords,
                    UseRegex ? SearchMode.RegEx : SearchMode.Normal);
                _matches = new TextSegmentCollection<TextSegment>(_document);
                foreach (var result in FindAll(0, _document.TextLength))
                {
                    if (_matches.Count >= MaxMatches)
                    {
                        _capped = true;
                        break;
                    }
                    _matches.Add(new TextSegment { StartOffset = result.Offset, Length = result.Length });
                }
            }
            catch (Exception ex) when (ex is SearchPatternException or ArgumentException or RegexMatchTimeoutException)
            {
                _strategy = null;
                DisconnectMatches();
                _error = ex.Message;
            }

            if (selectFirst && _strategy is not null)
            {
                int from = _editor.SelectionStart;
                var first = FindAll(from, _document.TextLength - from).FirstOrDefault()
                            ?? FindAll(0, _document.TextLength).FirstOrDefault();
                if (first is not null) Select(first);
                else _editor.Select(from, 0);
            }
        }

        _editor?.TextArea.TextView.InvalidateLayer(_renderer.Layer);
        UpdateStatus();
    }

    /// <summary>区间内的匹配项（不含空匹配，如正则表达式 <c>^</c>）。</summary>
    private IEnumerable<ISearchResult> FindAll(int offset, int length)
    {
        if (_strategy is null || _document is null || length < 0) return [];
        return _strategy.FindAll(_document, offset, length).Where(r => r.Length > 0);
    }

    public void FindNext()
    {
        if (!EnsureSearchable()) return;
        int from = _editor!.SelectionStart + _editor.SelectionLength;
        var result = FindAll(from, _document!.TextLength - from).FirstOrDefault()
                     ?? FindAll(0, _document.TextLength).FirstOrDefault(); // 到末尾后从头接着找
        if (result is not null) Select(result);
    }

    public void FindPrevious()
    {
        if (!EnsureSearchable()) return;
        int before = _editor!.SelectionStart;
        var result = FindAll(0, before).LastOrDefault()
                     ?? FindAll(0, _document!.TextLength).LastOrDefault(); // 到开头后从末尾接着找
        if (result is not null) Select(result);
    }

    /// <summary>替换当前选中的匹配项，然后选中下一个。选中的不是匹配项时只查找下一个。</summary>
    public void ReplaceCurrent()
    {
        if (!EnsureSearchable() || _editor!.IsReadOnly) return;
        int start = _editor.SelectionStart, length = _editor.SelectionLength;
        var current = length > 0
            ? FindAll(start, length).FirstOrDefault(r => r.Offset == start && r.Length == length)
            : null;
        if (current is not null)
        {
            var replacement = current.ReplaceWith(ReplacePattern);
            _document!.Replace(start, length, replacement);
            _editor.Select(start + replacement.Length, 0);
        }
        FindNext();
    }

    /// <summary>全部替换（一次修改，可以一次撤销）。返回替换的个数。</summary>
    public int ReplaceAll()
    {
        if (!EnsureSearchable() || _editor!.IsReadOnly) return 0;
        var document = _document!;
        var results = FindAll(0, document.TextLength).ToList();
        if (results.Count > 0)
        {
            var replacement = ReplacePattern;
            document.BeginUpdate();
            try
            {
                for (int i = results.Count - 1; i >= 0; i--)
                    document.Replace(results[i].Offset, results[i].Length, results[i].ReplaceWith(replacement));
            }
            finally { document.EndUpdate(); }
        }
        Refresh(selectFirst: false);
        _transientStatus = results.Count == 0 ? "无结果" : $"已替换 {results.Count:N0} 处";
        UpdateStatus();
        return results.Count;
    }

    private bool EnsureSearchable()
    {
        if (_editor is null || _document is null) return false;
        if (_refreshTimer.IsEnabled) Refresh(selectFirst: false); // 文档刚改过，还没重新查找
        return _strategy is not null;
    }

    private void Select(ISegment result)
    {
        var editor = _editor!;
        // 光标放在匹配项末尾（与 AvaloniaEdit 自带的查找相同）；超长的行据此显示光标附近的一段（见 LongLineWindow）
        editor.Select(result.Offset, result.Length);
        EditorScrolling.ScrollCaretIntoView(editor);
        AvoidMatch(result);
    }

    private void UpdateStatus()
    {
        string text;
        if (_transientStatus is not null) text = _transientStatus;
        else if (_error is not null) text = "表达式有误";
        else if (_matches is null) text = "";
        else if (_matches.Count == 0) text = "无结果";
        else
        {
            string total = _capped ? $"{_matches.Count:N0}+" : $"{_matches.Count:N0}";
            int index = CurrentIndex();
            text = index >= 0 ? $"{index + 1:N0} / {total}" : $"共 {total} 项";
        }
        StatusText.Text = text;
        ToolTip.SetTip(StatusText, _error);
        StatusText.Classes.Set("error", _error is not null || (_transientStatus is null && _matches is { Count: 0 }));
    }

    /// <summary>编辑器里选中的是第几个匹配项（不是匹配项时为 -1）。</summary>
    private int CurrentIndex()
    {
        if (_editor is null || _matches is null || _editor.SelectionLength == 0) return -1;
        int start = _editor.SelectionStart, length = _editor.SelectionLength;
        var segment = _matches.FindFirstSegmentWithStartAfter(start);
        if (segment is null || segment.StartOffset != start || segment.Length != length) return -1;
        int index = 0;
        for (var s = _matches.GetPreviousSegment(segment); s is not null; s = _matches.GetPreviousSegment(s)) index++;
        return index;
    }

    // ───────────────────────── 键盘 ─────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.Escape when mods == KeyModifiers.None:
                Close();
                break;
            case Key.Enter when ctrl && alt:
                ReplaceAll();
                break;
            case Key.Enter when !ctrl && !alt && e.Source is Visual v && IsInside(v, ReplaceBox):
                ReplaceCurrent();
                break;
            case Key.Enter when !ctrl && !alt:
                if (shift) FindPrevious(); else FindNext();
                break;
            case Key.C when alt && !ctrl && !shift:
                MatchCase = !MatchCase;
                break;
            case Key.W when alt && !ctrl && !shift:
                WholeWords = !WholeWords;
                break;
            case Key.R when alt && !ctrl && !shift:
                UseRegex = !UseRegex;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsInside(Visual v, Visual ancestor)
    {
        for (Visual? x = v; x is not null; x = x.GetVisualParent())
            if (x == ancestor) return true;
        return false;
    }

    // ───────────────────────── 拖动 ─────────────────────────
    // 按住按钮和输入框以外的地方拖动；双击空白处回到右上角。位置在本次运行中保留，并始终限制在编辑区内。

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Parent is not Visual parent) return;
        if (e.ClickCount == 2)
        {
            MoveTo(default); // 回到右上角
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(parent);
        _dragStartOffset = Offset;
        e.Pointer.Capture(Frame);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || Parent is not Visual parent) return;
        MoveTo(_dragStartOffset + (e.GetPosition(parent) - start));
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>移动面板。<paramref name="byUser"/>：用户放的位置（之后避开匹配项后回到这里）。</summary>
    internal void MoveTo(Vector offset, bool byUser = true)
    {
        var clamped = Clamp(offset);
        _offset.X = clamped.X;
        _offset.Y = clamped.Y;
        if (byUser) _home = clamped;
    }

    private void OnBoundsSizeChanged(object? sender, SizeChangedEventArgs e) => ClampOffset();

    private void ClampOffset() => MoveTo(Offset, byUser: false);

    /// <summary>面板在偏移为 <paramref name="offset"/> 时占的区域（父容器坐标）。</summary>
    private Rect PanelRectAt(Vector offset) => Bounds.Translate(offset);

    /// <summary>
    /// 跳到匹配项后：面板（在用户放的位置时）挡住了它，就挪到它的正下方或正上方（取挪动少的）；
    /// 不挡时回到用户放的位置。只在跳转时调用，用户手动把面板拖到匹配项上不管。
    /// </summary>
    private void AvoidMatch(ISegment match)
    {
        if (_editor is null || Parent is not Visual parent || Bounds.Width <= 0) return;
        _editor.UpdateLayout();
        var view = _editor.TextArea.TextView;
        Rect? target = null;
        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, match))
        {
            if (view.TranslatePoint(r.TopLeft, parent) is not { } p) continue;
            var rect = new Rect(p, r.Size);
            target = target is { } t ? t.Union(rect) : rect;
        }
        if (target is not { } m) return;
        const double gap = 8;
        var home = Clamp(_home);
        bool Blocks(Vector o) => PanelRectAt(o).Intersects(m.Inflate(2));
        if (!Blocks(home))
        {
            MoveTo(home, byUser: false);
            return;
        }

        Vector? best = null;
        double Distance(Vector o) => Math.Abs(o.X - home.X) + Math.Abs(o.Y - home.Y);
        void Consider(Vector o)
        {
            o = Clamp(o);
            if (!Blocks(o) && (best is not { } b || Distance(o) < Distance(b))) best = o;
        }
        Consider(new Vector(home.X, m.Bottom + gap - Bounds.Top));                  // 正下方
        Consider(new Vector(home.X, m.Top - gap - Bounds.Height - Bounds.Top));     // 正上方
        if (best is null)
        {
            Consider(new Vector(m.Left - gap - Bounds.Width - Bounds.Left, home.Y)); // 左边
            Consider(new Vector(m.Right + gap - Bounds.Left, home.Y));               // 右边
        }
        if (best is { } v) MoveTo(v, byUser: false);
    }

    /// <summary>限制偏移量，使面板整个留在编辑区内（面板比编辑区还大时贴住左上角）。</summary>
    private Vector Clamp(Vector offset)
    {
        var host = Bounds;                    // 不含 RenderTransform 的布局位置
        var area = DragBounds?.Bounds ?? default;
        if (host.Width <= 0 || area.Width <= 0) return offset;

        static double Clamp(double v, double min, double max) => max < min ? min : Math.Clamp(v, min, max);
        return new Vector(
            Clamp(offset.X, area.Left - host.Left, area.Right - host.Right),
            Clamp(offset.Y, area.Top - host.Top, area.Bottom - host.Bottom));
    }

    // ───────────────────────── 标出匹配项 ─────────────────────────

    /// <summary>
    /// 用橙色标出屏幕上的所有匹配项（包括当前项）。橙色上下对齐字形的上下边缘（见 <see cref="TextInkBand"/>），
    /// 比选中文字的蓝色（整行高）矮一些；
    /// 画在选中文字的下面，所以当前项是橙色上叠着蓝色，手动选中的其他文字只有蓝色。
    /// 超长的行只看显示出来的一段（折叠掉的部分里可能有几万个匹配项）。
    /// </summary>
    private sealed class MatchRenderer(FindPanel owner) : IBackgroundRenderer
    {
        /// <summary>半透明的橙色：浅色、深色主题下都保留文字本身的颜色。</summary>
        internal static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x8C, 0x00));

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var editor = owner._editor;
            if (editor is null || owner._strategy is null || owner._document is null || !textView.VisualLinesValid) return;
            var area = editor.TextArea;
            var brush = MatchBrush;
            double radius = area.SelectionCornerRadius;

            foreach (var line in textView.VisualLines)
            {
                var (start, end) = DisplayedRange(line);
                if (end <= start) continue;
                IEnumerable<ISegment> segments = owner._capped
                    ? owner.FindAll(start, end - start)
                    : owner._matches!.FindOverlappingSegments(start, end - start);
                foreach (var segment in segments)
                {
                    int from = Math.Max(segment.Offset, start), to = Math.Min(segment.EndOffset, end);
                    if (to <= from) continue;
                    var piece = new TextSegment { StartOffset = from, EndOffset = to };
                    int lineStart = line.FirstDocumentLine.Offset;
                    int startColumn = line.GetVisualColumn(from - lineStart), endColumn = line.GetVisualColumn(to - lineStart);
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, piece))
                        drawingContext.DrawRectangle(brush, null,
                            TextInkBand.Shrink(textView, line, rect, startColumn, endColumn), radius, radius);
                }
            }
        }

        /// <summary>这一显示行实际显示的文档区间（不含超长行折叠掉的部分）。</summary>
        private static (int Start, int End) DisplayedRange(VisualLine line)
        {
            int lineStart = line.FirstDocumentLine.Offset;
            int start = lineStart, end = line.LastDocumentLine.EndOffset;
            if (line.Elements.OfType<LongLineWindow.CollapsedText>().Any())
            {
                var shown = line.Elements.Where(e => e is not LongLineWindow.CollapsedText && e.DocumentLength > 0).ToList();
                if (shown.Count == 0) return (0, 0);
                start = lineStart + shown[0].RelativeTextOffset;
                end = lineStart + shown[^1].RelativeTextOffset + shown[^1].DocumentLength;
            }
            return (start, end);
        }
    }
}
