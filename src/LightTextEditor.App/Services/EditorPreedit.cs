using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace LightTextEditor.App.Services;

/// <summary>
/// 输入法正在组字的文字（如拼音“kan'bu'dao”）显示在光标处，带下划线。
/// <para>
/// AvaloniaEdit 自带的输入法客户端不支持组字（SupportsPreedit = false，组字文字直接丢弃），
/// 而 Avalonia 在 Windows 上不让输入法自己显示组字窗口，结果拼音哪里都看不到，只看得到候选词。
/// 这里给编辑器换上自己的输入法客户端：组字文字作为光标处的一段临时文字画出来，不写入文档
/// （不进撤销记录、不算字符数、不标记“未保存”）；输入法提交后照常由 AvaloniaEdit 插入文字。
/// </para>
/// </summary>
internal static class EditorPreedit
{
    private static readonly ConditionalWeakTable<TextArea, PreeditClient> Clients = new();

    public static void Install(TextArea textArea)
    {
        var client = new PreeditClient(textArea);
        Clients.AddOrUpdate(textArea, client);
        // AvaloniaEdit 用类处理程序提供自己的客户端；实例处理程序在它之后执行，把它换掉。
        // 只换编辑器自己的：放在编辑器里的输入框（如 AvaloniaEdit 自带的查找面板）的请求也会冒泡到这里，
        // 换掉它会让拼音和提交的文字跑到正文里
        textArea.AddHandler(InputElement.TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Source, textArea)) return;
            if (ImeDebugLog.IsEnabled)
                ImeDebugLog.Write("client", $"输入法请求客户端：原来是 {e.Client?.GetType().FullName ?? "null"}，" +
                                            (textArea.IsReadOnly ? "只读，不替换" : "替换为组字客户端"));
            if (!textArea.IsReadOnly) e.Client = client;
        }, RoutingStrategies.Bubble);
        if (ImeDebugLog.IsEnabled)
        {
            textArea.AddHandler(InputElement.TextInputEvent, (_, e) =>
                ImeDebugLog.Write("input", $"TextInput {ImeDebugLog.Quote(e.Text)} 光标={textArea.Caret.Offset} handled={e.Handled}"),
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    /// <summary>编辑器的输入法客户端（测试用）。</summary>
    internal static PreeditClient? ClientOf(TextArea textArea) => Clients.TryGetValue(textArea, out var c) ? c : null;

    internal sealed class PreeditClient : TextInputMethodClient
    {
        private readonly TextArea _textArea;
        private readonly PreeditGenerator _generator = new();
        private readonly PreeditCaretRenderer _caretRenderer;

        /// <summary>组字光标最近一次画在第几列（没有组字时画的是 null；测试用）。</summary>
        internal int? LastDrawnCaretColumn => _caretRenderer.LastDrawnColumn;

        public PreeditClient(TextArea textArea)
        {
            _textArea = textArea;
            textArea.TextView.ElementGenerators.Add(_generator);
            textArea.TextView.BackgroundRenderers.Add(_caretRenderer = new PreeditCaretRenderer(_generator, textArea));
            textArea.Caret.PositionChanged += (_, _) =>
            {
                RaiseCursorRectangleChanged();
                RaiseSurroundingTextChanged();
                RaiseSelectionChanged();
            };
        }

        /// <summary>正在组字的文字；没有时为 null。</summary>
        public string? PreeditText => _generator.Text;

        /// <summary>光标在组字文字中的位置。</summary>
        public int PreeditCursor => _generator.Cursor;

        public override Visual TextViewVisual => _textArea;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => true;

        /// <summary>周围文字最多取光标前后各这么多字符：超长的行（几百万字符）不必每次都整行复制给输入法。</summary>
        private const int SurroundingRadius = 1000;

        /// <summary>周围文字从行内第几个字符开始（行不长时为 0，即整行）。</summary>
        private int SurroundingStart
        {
            get
            {
                var line = _textArea.Document.GetLineByNumber(_textArea.Caret.Line);
                if (line.Length <= 2 * SurroundingRadius) return 0;
                int column = _textArea.Caret.Offset - line.Offset;
                int start = Math.Clamp(column - SurroundingRadius, 0, line.Length - 2 * SurroundingRadius);
                // 不从代理对中间开始
                if (start > 0 && char.IsLowSurrogate(_textArea.Document.GetCharAt(line.Offset + start))) start--;
                return start;
            }
        }

        public override string SurroundingText
        {
            get
            {
                var line = _textArea.Document.GetLineByNumber(_textArea.Caret.Line);
                int start = SurroundingStart;
                int length = Math.Min(line.Length - start, 2 * SurroundingRadius + 1);
                return _textArea.Document.GetText(line.Offset + start, length);
            }
        }

        /// <summary>光标位置（即组字文字的起点），输入法据此摆放候选窗口。与 AvaloniaEdit 自带的客户端相同。</summary>
        public override Rect CursorRectangle
        {
            get
            {
                var transform = _textArea.TextView.TransformToVisual(_textArea);
                if (transform is null) return default;
                var rect = _textArea.Caret.CalculateCaretRectangle().TransformToAABB(transform.Value);
                var scroll = _textArea.TextView.ScrollOffset;
                return rect.WithX(rect.X - scroll.X).WithY(rect.Y - scroll.Y);
            }
        }

        public override TextSelection Selection
        {
            get
            {
                int column = _textArea.Caret.Position.Column - SurroundingStart;
                return new TextSelection(column, column + _textArea.Selection.Length);
            }
            set
            {
                var selection = _textArea.Selection;
                if (selection.StartPosition.Line == 0) return;
                int start = SurroundingStart;
                _textArea.Selection = selection.StartSelectionOrSetEndpoint(
                    new TextViewPosition(selection.StartPosition.Line, value.Start + start),
                    new TextViewPosition(selection.StartPosition.Line, value.End + start));
            }
        }

        public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

        public override void SetPreeditText(string? preeditText, int? cursorPos)
        {
            int? oldOffset = _generator.Text is null ? null : _generator.Offset;
            string? oldText = _generator.Text;
            if (string.IsNullOrEmpty(preeditText))
            {
                if (ImeDebugLog.IsEnabled)
                    ImeDebugLog.Write("client", $"SetPreeditText({ImeDebugLog.Quote(preeditText)}, {cursorPos?.ToString() ?? "null"}) → 清除组字");
                if (oldOffset is null) return;
                _generator.Text = null;
                Redraw(oldOffset.Value);
                if (_textArea.IsKeyboardFocusWithin) _textArea.Caret.Show();
                return;
            }

            // 光标位置用输入法报告的（与 Avalonia 的 TextBox 相同）；没有报告时在末尾
            int cursor = cursorPos ?? preeditText.Length;

            _generator.Text = preeditText;
            _generator.Offset = _textArea.Caret.Offset;
            _generator.Cursor = Math.Clamp(cursor, 0, preeditText.Length);
            if (ImeDebugLog.IsEnabled)
                ImeDebugLog.Write("client", $"SetPreeditText({ImeDebugLog.Quote(preeditText)}, {cursorPos?.ToString() ?? "null"}) " +
                                            $"上次={ImeDebugLog.Quote(oldText)} → 光标 {_generator.Cursor}，文档位置 {_generator.Offset}");
            // 组字期间不显示编辑器的光标（它停在组字文字前面），改为在组字文字里画（见 PreeditCaretRenderer）
            _textArea.Caret.Hide();
            if (oldOffset is { } old && old != _generator.Offset) Redraw(old);
            Redraw(_generator.Offset);
        }

        private void Redraw(int offset)
        {
            var document = _textArea.Document;
            if (document is null) return;
            _textArea.TextView.Redraw(document.GetLineByOffset(Math.Clamp(offset, 0, document.TextLength)));
        }
    }

    /// <summary>在组字文字所在的文档位置插入一段不占文档长度的显示元素。</summary>
    internal sealed class PreeditGenerator : VisualLineElementGenerator
    {
        public string? Text { get; set; }
        public int Offset { get; set; }
        public int Cursor { get; set; }

        public override int GetFirstInterestedOffset(int startOffset) =>
            Text is not null && Offset >= startOffset ? Offset : -1;

        public override VisualLineElement? ConstructElement(int offset) =>
            Text is not null && offset == Offset ? new PreeditElement(Text) : null;
    }

    /// <summary>组字文字：沿用编辑器的字体和颜色，加下划线；可以随自动换行折到下一行。</summary>
    internal sealed class PreeditElement(string text) : VisualLineElement(text.Length, 0)
    {
        public string Text { get; } = text;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            return new TextCharacters(Text.AsMemory(startVisualColumn - VisualColumn), TextRunProperties);
        }
    }

    /// <summary>
    /// 组字期间的光标：画在组字文字里输入法指定的位置（通常在末尾）。
    /// 画在背景层：重建显示行（组字文字每次变化都会）之后 TextView 会重画背景层；
    /// 光标层（KnownLayer.Caret）只在编辑器自己的光标移动、闪烁时才重画，画在那里会一直停在第一次画的位置。
    /// </summary>
    private sealed class PreeditCaretRenderer(PreeditGenerator generator, TextArea textArea) : IBackgroundRenderer
    {
        private string? _lastLogged;

        public int? LastDrawnColumn { get; private set; }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            LastDrawnColumn = null;
            if (generator.Text is null || !textView.VisualLinesValid) return;
            foreach (var line in textView.VisualLines)
            {
                if (line.Elements.OfType<PreeditElement>().FirstOrDefault() is not { } element) continue;
                int column = element.VisualColumn + generator.Cursor;
                var top = line.GetVisualPosition(column, VisualYPosition.TextTop) - textView.ScrollOffset;
                var bottom = line.GetVisualPosition(column, VisualYPosition.TextBottom) - textView.ScrollOffset;
                var brush = textArea.CaretBrush ?? textArea.Foreground ?? Brushes.Black;
                var x = Math.Round(top.X) + 0.5;
                if (ImeDebugLog.IsEnabled)
                {
                    var start = line.GetVisualPosition(element.VisualColumn, VisualYPosition.TextTop) - textView.ScrollOffset;
                    var entry = $"组字 {ImeDebugLog.Quote(generator.Text)} 光标 {generator.Cursor}：画在第 {column} 列 x={x:F1}（组字文字从 x={start.X:F1} 开始）";
                    if (entry != _lastLogged) ImeDebugLog.Write("draw", _lastLogged = entry);
                }
                drawingContext.DrawLine(new Pen(brush, 1), new Point(x, top.Y), new Point(x, bottom.Y));
                LastDrawnColumn = column;
                return;
            }
        }
    }
}
