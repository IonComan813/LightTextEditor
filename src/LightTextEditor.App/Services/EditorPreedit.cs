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
        // AvaloniaEdit 用类处理程序提供自己的客户端；实例处理程序在它之后执行，把它换掉
        textArea.AddHandler(InputElement.TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (!textArea.IsReadOnly) e.Client = client;
        }, RoutingStrategies.Bubble);
    }

    /// <summary>编辑器的输入法客户端（测试用）。</summary>
    internal static PreeditClient? ClientOf(TextArea textArea) => Clients.TryGetValue(textArea, out var c) ? c : null;

    internal sealed class PreeditClient : TextInputMethodClient
    {
        private readonly TextArea _textArea;
        private readonly PreeditGenerator _generator = new();

        public PreeditClient(TextArea textArea)
        {
            _textArea = textArea;
            textArea.TextView.ElementGenerators.Add(_generator);
            textArea.TextView.BackgroundRenderers.Add(new PreeditCaretRenderer(_generator, textArea));
            textArea.Caret.PositionChanged += (_, _) =>
            {
                RaiseCursorRectangleChanged();
                RaiseSurroundingTextChanged();
                RaiseSelectionChanged();
            };
        }

        /// <summary>输入法上一次报告的光标位置（见 SetPreeditText）。</summary>
        private int? _reportedCursor;

        /// <summary>正在组字的文字；没有时为 null。</summary>
        public string? PreeditText => _generator.Text;

        /// <summary>光标在组字文字中的位置。</summary>
        public int PreeditCursor => _generator.Cursor;

        public override Visual TextViewVisual => _textArea;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => true;

        public override string SurroundingText
        {
            get
            {
                var line = _textArea.Document.GetLineByNumber(_textArea.Caret.Line);
                return _textArea.Document.GetText(line.Offset, line.Length);
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
                int column = _textArea.Caret.Position.Column;
                return new TextSelection(column, column + _textArea.Selection.Length);
            }
            set
            {
                var selection = _textArea.Selection;
                if (selection.StartPosition.Line == 0) return;
                _textArea.Selection = selection.StartSelectionOrSetEndpoint(
                    new TextViewPosition(selection.StartPosition.Line, value.Start),
                    new TextViewPosition(selection.StartPosition.Line, value.End));
            }
        }

        public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

        public override void SetPreeditText(string? preeditText, int? cursorPos)
        {
            int? oldOffset = _generator.Text is null ? null : _generator.Offset;
            string? oldText = _generator.Text;
            int? oldReported = _reportedCursor;
            _reportedCursor = cursorPos;
            if (string.IsNullOrEmpty(preeditText))
            {
                if (oldOffset is null) return;
                _generator.Text = null;
                Redraw(oldOffset.Value);
                if (_textArea.IsKeyboardFocusWithin) _textArea.Caret.Show();
                return;
            }

            // Avalonia（Windows）在输入法这次没有报告光标位置时沿用上一次的值：微软拼音继续打字时只报告文字，
            // 光标位置会一直停在第一个字母后面。文字变了而报告的位置没变，就当它是旧值，让光标随文字长度移动
            // （在末尾打字时光标保持在末尾；用方向键移到中间后再打字，光标跟着往后走）。
            int cursor = cursorPos ?? preeditText.Length;
            if (cursorPos is not null && oldText is not null && preeditText != oldText && cursorPos == oldReported)
                cursor = _generator.Cursor + (preeditText.Length - oldText.Length);

            _generator.Text = preeditText;
            _generator.Offset = _textArea.Caret.Offset;
            _generator.Cursor = Math.Clamp(cursor, 0, preeditText.Length);
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

    /// <summary>组字期间的光标：画在组字文字里输入法指定的位置（通常在末尾）。</summary>
    private sealed class PreeditCaretRenderer(PreeditGenerator generator, TextArea textArea) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (generator.Text is null || !textView.VisualLinesValid) return;
            foreach (var line in textView.VisualLines)
            {
                if (line.Elements.OfType<PreeditElement>().FirstOrDefault() is not { } element) continue;
                int column = element.VisualColumn + generator.Cursor;
                var top = line.GetVisualPosition(column, VisualYPosition.TextTop) - textView.ScrollOffset;
                var bottom = line.GetVisualPosition(column, VisualYPosition.TextBottom) - textView.ScrollOffset;
                var brush = textArea.CaretBrush ?? textArea.Foreground ?? Brushes.Black;
                var x = Math.Round(top.X) + 0.5;
                drawingContext.DrawLine(new Pen(brush, 1), new Point(x, top.Y), new Point(x, bottom.Y));
                return;
            }
        }
    }
}
