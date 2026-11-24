using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace LightTextEditor.App.Services;

/// <summary>
/// 超长的行（如压缩成一行的 JSON）只显示光标附近的一段，前后折叠成“前面还有 N 个字符”“后面还有 N 个字符”。
/// <para>
/// AvaloniaEdit 每次重建一行都要把整行交给排版引擎：一行几百万字符时，打开、每按一个键都要几秒，界面“未响应”。
/// 这里在显示时把这一行拆成 [折叠][光标附近的一段][折叠]，只排版中间一段；文档内容不变，
/// 复制、保存、搜索、撤销都照常。光标走到这一段的边缘附近时（方向键、翻页、搜索、跳转等）重新取一段，
/// 并保持光标在屏幕上的位置不动；点击折叠处会接着显示那一边的内容。
/// </para>
/// <para>
/// 文档里有这样的行时整篇不做语法高亮（见 <see cref="EditorHighlighter"/>）。
/// AvaloniaEdit 12.0.0 对 3000 字符以上的行不运行元素生成器，整行是一个 <see cref="VisualLineText"/>，
/// 所以这里用行变换器（<see cref="IVisualLineTransformer"/>）在排版之前拆分它。
/// （AvaloniaEdit 主干上已有自己的长行截断，尚未发布；升级时要重新看这里。）
/// </para>
/// </summary>
internal sealed class LongLineWindow : IVisualLineTransformer
{
    /// <summary>超过这个长度的行才按窗口显示（与 TextMate 不再着色的长度相同）。更短的行整行排版也只要几毫秒。</summary>
    internal const int Threshold = 10_000;

    /// <summary>窗口半宽的下限（字符数）。</summary>
    private const int MinHalfWindow = 5_000;

    /// <summary>
    /// 窗口半宽 = 一屏能显示的字符数 × 这个倍数。光标离窗口边缘不足半宽的一半（1.25 屏）时重新取窗口，
    /// 所以按一次 PageDown / PageUp（一屏）不会走出窗口。
    /// </summary>
    private const double PagesPerHalfWindow = 2.5;

    private static readonly ConditionalWeakTable<TextArea, LongLineWindow> Installed = new();

    private readonly TextEditor _editor;
    private readonly TextArea _textArea;

    /// <summary>每个长行当前显示的一段（相对行首的偏移）。行被删除后自动回收。</summary>
    private readonly ConditionalWeakTable<DocumentLine, Range> _windows = new();

    private sealed class Range(int start, int end)
    {
        public int Start = start;
        public int End = end;
    }

    private LongLineWindow(TextEditor editor)
    {
        _editor = editor;
        _textArea = editor.TextArea;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    public static void Install(TextEditor editor)
    {
        if (Installed.TryGetValue(editor.TextArea, out _)) return;
        var window = new LongLineWindow(editor);
        Installed.Add(editor.TextArea, window);
        editor.TextArea.TextView.LineTransformers.Add(window);
    }

    /// <summary>
    /// 放到其他行变换器（语法高亮等）之后：折叠提示的 TextRunProperties 由 AvaloniaEdit 内部设置，这里设置不了，
    /// 之后的变换器若给它改颜色会出错。安装语法高亮后调用。
    /// </summary>
    public static void MoveToEnd(TextEditor editor)
    {
        if (!Installed.TryGetValue(editor.TextArea, out var window)) return;
        var transformers = editor.TextArea.TextView.LineTransformers;
        if (transformers.Count > 0 && transformers[^1] == window) return;
        transformers.Remove(window);
        transformers.Add(window);
    }

    /// <summary>这一行当前显示的一段（相对行首；测试用）。不是长行或还没显示过时为 null。</summary>
    internal static (int Start, int End)? WindowOf(TextArea textArea, DocumentLine line) =>
        Installed.TryGetValue(textArea, out var w) && w._windows.TryGetValue(line, out var r) ? (r.Start, r.End) : null;

    private TextView TextView => _textArea.TextView;

    /// <summary>窗口半宽：至少 <see cref="MinHalfWindow"/>，并且够 2.5 屏。</summary>
    private int HalfWindow()
    {
        var view = TextView;
        double charWidth = Math.Max(1, view.WideSpaceWidth);
        double columns = Math.Max(1, view.Bounds.Width / charWidth);
        // 自动换行时一屏是 行数 × 列数；不换行时长行只占一行，一屏是列数
        double page = _editor.WordWrap
            ? columns * Math.Max(1, view.Bounds.Height / Math.Max(1, view.DefaultLineHeight))
            : columns;
        return (int)Math.Max(MinHalfWindow, Math.Min(int.MaxValue / 4, page * PagesPerHalfWindow));
    }

    /// <summary>光标离窗口两端都还够远（或那一端就是行首 / 行尾），不需要重新取窗口。</summary>
    private static bool IsComfortable(Range r, int caret, int lineLength, int half)
    {
        int margin = half / 2;
        return caret >= r.Start && caret <= r.End
            && (r.Start == 0 || caret - r.Start >= margin)
            && (r.End == lineLength || r.End - caret >= margin);
    }

    /// <summary>以 <paramref name="caret"/> 为中心取一段（长度 2 × 半宽，靠近行首 / 行尾时贴边）。</summary>
    private static Range Centered(int caret, int lineLength, int half)
    {
        int start = Math.Clamp(caret - half, 0, Math.Max(0, lineLength - 2 * half));
        return new Range(start, Math.Min(lineLength, start + 2 * half));
    }

    /// <summary>
    /// 这一行要显示的一段。光标在这一行上时保证包含光标（否则 AvaloniaEdit 会把光标挪到折叠处的边上）。
    /// </summary>
    private Range WindowFor(DocumentLine line, int half)
    {
        int length = line.Length;
        int caret = _textArea.Caret.Offset - line.Offset; // 不触发光标校验
        bool caretHere = caret >= 0 && caret <= length;

        int center = caretHere ? caret : 0;
        if (_windows.TryGetValue(line, out var r))
        {
            // 行被编辑过：收进行内。删掉了很多字、窗口几乎空了时重新取
            r.End = Math.Min(r.End, length);
            r.Start = Math.Min(r.Start, r.End);
            bool usable = r.End - r.Start >= Math.Min(length, half);
            if (usable && (!caretHere || IsComfortable(r, caret, length, half))) return r;
            _windows.Remove(line);
            if (!caretHere) center = r.Start;
        }

        r = Centered(center, length, half);
        _windows.Add(line, r);
        return r;
    }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        var visualLine = context.VisualLine;
        var line = visualLine.FirstDocumentLine;
        if (line.Length <= Threshold || visualLine.LastDocumentLine != line) return;

        var window = WindowFor(line, HalfWindow());
        var document = context.Document;
        int start = AvoidSplittingSurrogates(document, line.Offset, window.Start);
        int end = AvoidSplittingSurrogates(document, line.Offset, window.End);

        start = SplitAt(elements, start, roundDown: true);
        end = SplitAt(elements, end, roundDown: false);

        // 先折叠后面（不影响前面元素的下标），再折叠前面
        if (end < line.Length)
            Collapse(context, elements, end, line.Length, before: false);
        if (start > 0)
            Collapse(context, elements, 0, start, before: true);
    }

    /// <summary>窗口边界不要落在代理对中间（否则一个字会被拆成两半显示成乱码）。</summary>
    private static int AvoidSplittingSurrogates(TextDocument document, int lineOffset, int relative)
    {
        if (relative <= 0 || relative >= document.TextLength - lineOffset) return relative;
        return char.IsLowSurrogate(document.GetCharAt(lineOffset + relative)) ? relative - 1 : relative;
    }

    /// <summary>
    /// 在相对行首的 <paramref name="offset"/> 处把元素一分为二，返回实际的分界（元素不能拆分时取它的起点或终点）。
    /// </summary>
    private static int SplitAt(IList<VisualLineElement> elements, int offset, bool roundDown)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            int from = e.RelativeTextOffset, to = from + e.DocumentLength;
            if (offset <= from) return offset;
            if (offset >= to) continue;
            if (e.CanSplit)
            {
                e.Split(e.GetVisualColumn(offset), elements, i);
                return offset;
            }
            return roundDown ? from : to;
        }
        return offset;
    }

    /// <summary>把完全落在 [<paramref name="from"/>, <paramref name="to"/>) 里的元素换成一个折叠提示。</summary>
    private void Collapse(ITextRunConstructionContext context, IList<VisualLineElement> elements, int from, int to, bool before)
    {
        int first = -1, count = 0, length = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.RelativeTextOffset >= from && e.RelativeTextOffset + e.DocumentLength <= to && e.DocumentLength > 0)
            {
                if (first < 0) first = i;
                count++;
                length += e.DocumentLength;
            }
            else if (first >= 0) break;
        }
        if (count == 0) return;

        var text = before ? $"⋯ 前面还有 {length:N0} 个字符（点击显示）  " : $"  后面还有 {length:N0} 个字符（点击显示）⋯";
        var global = context.GlobalTextRunProperties;
        var properties = new GenericTextRunProperties(global.Typeface, global.FontRenderingEmSize,
            foregroundBrush: PlaceholderBrush, cultureInfo: global.CultureInfo);
        var textLine = FormattedTextElement.PrepareText(TextFormatter.Current, text, properties);
        context.VisualLine.ReplaceElement(first, count,
            new CollapsedText(this, context.VisualLine.FirstDocumentLine, textLine, properties, length, before));
    }

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));

    /// <summary>
    /// 光标移动后：如果光标走到了窗口边缘附近（或走出了窗口），重新取一段，并让光标在屏幕上的位置保持不动，
    /// 免得内容跳动。必须在 AvaloniaEdit 用旧的显示行校验光标之前做（它会把落在折叠处里的光标挪到边上）。
    /// </summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var document = _textArea.Document;
        if (document is null) return;
        int offset = _textArea.Caret.Offset;
        var line = document.GetLineByOffset(offset);
        if (line.Length <= Threshold) return;
        var visualLine = TextView.GetVisualLine(line.LineNumber);
        if (visualLine is null || !_windows.TryGetValue(line, out var window)) return; // 还没显示过：显示时会以光标为中心取
        int half = HalfWindow();
        int caret = offset - line.Offset;
        if (IsComfortable(window, caret, line.Length, half)) return;

        var position = new TextViewPosition(document.GetLocation(offset));
        bool wasVisible = caret >= window.Start && caret <= window.End;
        Point? before = wasVisible ? TextView.GetVisualPosition(position, VisualYPosition.LineTop) : null;

        _windows.Remove(line);
        _windows.Add(line, Centered(caret, line.Length, half));
        TextView.Redraw(visualLine);

        if (before is not { } old) return; // 跳到了很远处：由跳转命令把光标滚到可见处
        var now = TextView.GetVisualPosition(position, VisualYPosition.LineTop); // 立即按新窗口重建这一行
        var delta = now - old;
        if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) return;
        var scrollable = (ILogicalScrollable)TextView;
        var target = TextView.ScrollOffset + delta;
        scrollable.Offset = new Vector(Math.Max(0, target.X), Math.Max(0, target.Y));
        scrollable.RaiseScrollInvalidated(EventArgs.Empty);
    }

    /// <summary>点击折叠处：把光标放到窗口紧挨着它的一端，窗口随之移过去，接着显示那一边的内容。</summary>
    private void Reveal(CollapsedText element)
    {
        var line = element.Line;
        if (line.IsDeleted || !_windows.TryGetValue(line, out var window)) return;
        int target = line.Offset + (element.IsBefore ? window.Start : window.End);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = target; // 光标到了窗口边缘：OnCaretPositionChanged 以它为中心重新取一段
        _textArea.Caret.BringCaretToView();
        _textArea.Focus();
    }

    /// <summary>折叠掉的一段文字：显示为一句灰色提示，可以点击。</summary>
    internal sealed class CollapsedText(LongLineWindow owner, DocumentLine line, TextLine text, TextRunProperties properties,
        int documentLength, bool before) : FormattedTextElement(text, documentLength)
    {
        public DocumentLine Line { get; } = line;

        /// <summary>折叠的是窗口前面的部分（否则是后面的）。</summary>
        public bool IsBefore { get; } = before;

        // 用自己的灰色样式画（元素本身的 TextRunProperties 是空的，见 MoveToEnd）
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new FormattedTextRun(this, properties);

        protected override void OnQueryCursor(PointerEventArgs e)
        {
            if (e.Source is InputElement element) element.Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.Handled || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            owner.Reveal(this);
        }
    }
}
