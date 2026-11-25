using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>查找 / 替换面板（代替 AvaloniaEdit 自带的查找面板）。</summary>
public class FindPanelTests
{
    private static (MainWindow Window, DocumentTab Tab) CreateWindow(string text)
    {
        var library = Path.Combine(TestAppBuilder.Root, "find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        tab.Doc.Document.Text = text;
        tab.Editor.TextArea.Focus();
        Render();
        return (window, tab);
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Render();
    }

    [AvaloniaFact]
    public void OpeningWithSelection_DoesNotFillTheSearchBox()
    {
        var (window, tab) = CreateWindow("hello world");
        tab.Editor.Select(0, 5);
        Press(window, Key.F, RawInputModifiers.Control);

        var panel = window.FindPanel;
        Assert.True(panel.IsOpen);
        Assert.Equal("", panel.SearchPattern);
        Assert.True(panel.FindTextBox.IsFocused);
    }

    [AvaloniaFact]
    public void SearchBox_IsNotInsideTheEditor_SoTheImeTalksToTheTextBox()
    {
        var (window, tab) = CreateWindow("hello world");
        Press(window, Key.F, RawInputModifiers.Control);
        var panel = window.FindPanel;
        Assert.DoesNotContain(tab.Editor, panel.FindTextBox.GetVisualAncestors());

        // 输入法向查找框要客户端：得到的是输入框自己的，不是编辑器的组字客户端
        var request = (TextInputMethodClientRequestedEventArgs)Activator.CreateInstance(typeof(TextInputMethodClientRequestedEventArgs), nonPublic: true)!;
        request.RoutedEvent = InputElement.TextInputMethodClientRequestedEvent;
        panel.FindTextBox.RaiseEvent(request);
        Assert.IsNotType<EditorPreedit.PreeditClient>(request.Client);

        // 输入的文字只进查找框
        window.KeyTextInput("世界");
        Render();
        Assert.Equal("世界", panel.SearchPattern);
        Assert.Equal("hello world", tab.Doc.Document.Text);
    }

    [AvaloniaFact]
    public void Typing_SelectsTheFirstMatch_EnterGoesToTheNext()
    {
        var (window, tab) = CreateWindow("cat dog cat dog cat");
        Press(window, Key.F, RawInputModifiers.Control);
        window.KeyTextInput("cat");
        Render();
        var panel = window.FindPanel;
        var editor = tab.Editor;
        Assert.Equal(0, editor.SelectionStart);
        Assert.Equal(3, editor.SelectionLength);
        Assert.Equal("1 / 3", panel.StatusMessage);

        Press(window, Key.Enter);
        Assert.Equal(8, editor.SelectionStart);
        Assert.Equal("2 / 3", panel.StatusMessage);

        Press(window, Key.Enter);
        Press(window, Key.Enter); // 到末尾后从头接着找
        Assert.Equal(0, editor.SelectionStart);

        Press(window, Key.Enter, RawInputModifiers.Shift); // 上一个：从开头绕到末尾
        Assert.Equal(16, editor.SelectionStart);

        Press(window, Key.F3, RawInputModifiers.Shift);
        Assert.Equal(8, editor.SelectionStart);
    }

    [AvaloniaFact]
    public void Options_MatchCaseWholeWordsRegex()
    {
        var (window, _) = CreateWindow("Cat cat category");
        Press(window, Key.F, RawInputModifiers.Control);
        var panel = window.FindPanel;
        panel.SearchPattern = "cat";
        Assert.Equal(3, panel.MatchCount);
        panel.MatchCase = true;
        Assert.Equal(2, panel.MatchCount);
        panel.WholeWords = true;
        Assert.Equal(1, panel.MatchCount);
        panel.MatchCase = panel.WholeWords = false;
        panel.UseRegex = true;
        panel.SearchPattern = "c[a-z]+";
        Assert.Equal(3, panel.MatchCount);
        panel.SearchPattern = "c(";
        Assert.Equal("表达式有误", panel.StatusMessage);
        panel.SearchPattern = "^"; // 只有空匹配：不算
        Assert.Equal("无结果", panel.StatusMessage);
    }

    [AvaloniaFact]
    public void Replace_AndReplaceAll_AreUndoable()
    {
        var (window, tab) = CreateWindow("a1 b2 a3");
        Press(window, Key.H, RawInputModifiers.Control);
        var panel = window.FindPanel;
        Assert.True(panel.IsReplaceMode);
        panel.UseRegex = true;
        panel.SearchPattern = @"a(\d)";
        panel.ReplacePattern = "x$1";
        panel.ReplaceCurrent();
        Assert.Equal("x1 b2 a3", tab.Doc.Document.Text);
        Assert.Equal(6, tab.Editor.SelectionStart); // 选中了下一个

        tab.Editor.Undo();
        Assert.Equal("a1 b2 a3", tab.Doc.Document.Text);

        Assert.Equal(2, panel.ReplaceAll());
        Assert.Equal("x1 b2 x3", tab.Doc.Document.Text);
        Assert.Equal("已替换 2 处", panel.StatusMessage);
        tab.Editor.Undo(); // 全部替换一次撤销
        Assert.Equal("a1 b2 a3", tab.Doc.Document.Text);
    }

    [AvaloniaFact]
    public void Matches_AreHighlightedInOrange_TextHigh_UnderTheSelection()
    {
        var (window, tab) = CreateWindow("abc abc abc");
        Press(window, Key.F, RawInputModifiers.Control);
        window.FindPanel.SearchPattern = "abc";
        Render();
        var view = tab.Editor.TextArea.TextView;
        var renderer = Assert.Single(view.BackgroundRenderers, r => r.GetType().DeclaringType == typeof(FindPanel));
        Assert.Equal(AvaloniaEdit.Rendering.KnownLayer.Selection, renderer.Layer); // 选中文字画在它上面
        var selection = Assert.IsAssignableFrom<ISolidColorBrush>(tab.Editor.TextArea.SelectionBrush);
        var group = new DrawingGroup();
        using (var context = group.Open()) renderer.Draw(view, context);
        var drawn = group.Children.OfType<GeometryDrawing>().ToList();
        Assert.Equal(3, drawn.Count); // 当前项（选中的）也画橙色
        double lineHeight = view.DefaultLineHeight;
        Assert.All(drawn, d =>
        {
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(d.Brush);
            Assert.NotEqual(selection.Color, brush.Color);
            Assert.True(brush.Color.R > brush.Color.G && brush.Color.G > brush.Color.B); // 橙色
            var bounds = d.Geometry!.Bounds;
            Assert.InRange(bounds.Height, 4, lineHeight - 1); // 比整行矮
        });

        Press(window, Key.Escape);
        Assert.False(window.FindPanel.IsOpen);
        Assert.DoesNotContain(view.BackgroundRenderers, r => r.GetType().DeclaringType == typeof(FindPanel));
        Assert.True(tab.Editor.TextArea.IsFocused);
    }

    [AvaloniaFact]
    public void Panel_IsOpaque_AndCanBeDragged()
    {
        var (window, _) = CreateWindow("text");
        Press(window, Key.F, RawInputModifiers.Control);
        var panel = window.FindPanel;
        Assert.Equal(1, panel.Opacity);
        var frame = panel.Frame;
        var background = Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background);
        Assert.Equal(1, background.Opacity);
        Assert.Equal(255, background.Color.A);

        // 按住面板空白处拖动
        var corner = frame.TranslatePoint(new Point(frame.Bounds.Width - 2, frame.Bounds.Height - 2), window)!.Value;
        window.MouseDown(corner, MouseButton.Left);
        window.MouseMove(corner + new Vector(-200, 150));
        window.MouseUp(corner + new Vector(-200, 150), MouseButton.Left);
        Render();
        Assert.Equal(new Vector(-200, 150), panel.Offset);

        // 不能拖出编辑区（默认位置离编辑区右边 24、上边 8）
        panel.MoveTo(new Vector(5000, -5000));
        Assert.Equal(new Vector(24, -8), panel.Offset);
    }
}

public class FindPanelScrollTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindNext_ScrollsTheMatchIntoView(bool wordWrap)
    {
        var library = Path.Combine(TestAppBuilder.Root, "findscroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, WordWrap = wordWrap };
        var window = new MainWindow { Width = 900, Height = 500 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        var rng = new Random(1);
        var lines = Enumerable.Range(0, 600).Select(i =>
            string.Concat(Enumerable.Repeat("文字 text ", rng.Next(1, 60))) + (i % 37 == 5 ? " NEEDLE" : "")).ToList();
        tab.Doc.Document.Text = string.Join("\n", lines);
        tab.Editor.TextArea.Focus();
        Render();

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
        Render();
        window.FindPanel.SearchPattern = "NEEDLE";
        Render();
        var failures = new List<string>();
        for (int k = 0; k < 40; k++)
        {
            bool up = k >= 20;
            window.KeyPress(Key.Enter, up ? RawInputModifiers.Shift : RawInputModifiers.None, PhysicalKey.None, null);
            Render();
            var view = tab.Editor.TextArea.TextView;
            var caret = tab.Editor.TextArea.Caret.Position;
            var top = view.GetVisualPosition(caret, AvaloniaEdit.Rendering.VisualYPosition.LineTop).Y - view.ScrollOffset.Y;
            var bottom = view.GetVisualPosition(caret, AvaloniaEdit.Rendering.VisualYPosition.LineBottom).Y - view.ScrollOffset.Y;
            if (top < 0 || bottom > view.Bounds.Height)
                failures.Add($"{(up ? "上" : "下")} 第 {k} 次：行 {caret.Line} 显示在 {top:F0}..{bottom:F0}（可见 0..{view.Bounds.Height:F0}）");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

public class FindPanelAvoidTests
{
    [AvaloniaFact]
    public void Jumping_MovesThePanelOffTheMatch_AndBackWhenItNoLongerBlocks()
    {
        var library = Path.Combine(TestAppBuilder.Root, "findavoid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        var lines = Enumerable.Range(0, 40).Select(i => "line " + i).ToList();
        lines[1] = new string('x', 70) + " NEEDLE"; // 在右上角，面板下面
        lines[20] = "NEEDLE";                      // 在左边，不挡
        tab.Doc.Document.Text = string.Join("\n", lines);
        tab.Editor.TextArea.Focus();
        Render();

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
        Render();
        var panel = window.FindPanel;
        var home = panel.Offset;
        window.KeyTextInput("NEEDLE"); // 边输入边查找：跳到第 2 行的匹配项
        Render();
        Assert.Equal(2, tab.Editor.TextArea.Caret.Line);
        Assert.False(Overlaps(window, tab, panel), "面板挡住了匹配项");
        Assert.NotEqual(home, panel.Offset);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null); // 下一个在左边：面板回到原处
        Render();
        Assert.Equal(21, tab.Editor.TextArea.Caret.Line);
        Assert.Equal(home, panel.Offset);

        // 用户手动把面板放到匹配项上：不跳转就不挪
        tab.Editor.Select(tab.Doc.Document.GetLineByNumber(2).Offset + 71, 6);
        panel.MoveTo(home);
        Render();
        Assert.True(Overlaps(window, tab, panel));
        Assert.Equal(home, panel.Offset);
    }

    private static bool Overlaps(MainWindow window, DocumentTab tab, FindPanel panel)
    {
        var view = tab.Editor.TextArea.TextView;
        var topLeft = panel.TranslatePoint(default, window)!.Value;
        var panelRect = new Rect(topLeft, panel.Bounds.Size);
        var segment = new AvaloniaEdit.Document.TextSegment { StartOffset = tab.Editor.SelectionStart, Length = tab.Editor.SelectionLength };
        return AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(view, segment)
            .Any(r => new Rect(view.TranslatePoint(r.TopLeft, window)!.Value, r.Size).Intersects(panelRect));
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

public class FindHighlightInkTests
{
    /// <summary>橙色的上下边缘对齐字形的上下边缘（中文、英文各自按画它的字体），并且在整行高的选中背景之内。</summary>
    [AvaloniaTheory]
    [InlineData("狐狸", "敏捷的棕色狐狸跳过了懒狗", false)]
    [InlineData("HdgHdg", "xx HdgHdg yy", false)]
    // 字体里没有参考字（如 CI 的 Ubuntu 没有中文字体，中文画成方框）：用实际画出的墨迹
    [InlineData("狐狸", "敏捷的棕色狐狸跳过了懒狗", true)]
    [InlineData("HdgHdg", "xx HdgHdg yy", true)]
    public void OrangeEdges_MatchTheGlyphInk(string needle, string text, bool fontLacksReference)
    {
        var original = LightTextEditor.App.Services.TextInkBand.ReferenceBand;
        if (fontLacksReference) LightTextEditor.App.Services.TextInkBand.ReferenceBand = (_, _, _) => null;
        try { CheckOrangeEdges(needle, text); }
        finally { LightTextEditor.App.Services.TextInkBand.ReferenceBand = original; }
    }

    private static void CheckOrangeEdges(string needle, string text)
    {
        var library = Path.Combine(TestAppBuilder.Root, "findink-" + Guid.NewGuid().ToString("N"));
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        tab.Doc.Document.Text = text;
        tab.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        window.FindPanel.SearchPattern = needle;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var view = tab.Editor.TextArea.TextView;
        var renderer = view.BackgroundRenderers.Single(r => r.GetType().DeclaringType == typeof(FindPanel));
        var group = new DrawingGroup();
        using (var context = group.Open()) renderer.Draw(view, context);
        var orange = Assert.Single(group.Children.OfType<GeometryDrawing>()).Geometry!.Bounds;

        // 画出这几个字的那段文字的实际墨迹（行坐标）
        var line = view.VisualLines[0];
        var textLine = line.TextLines[0];
        int start = text.IndexOf(needle, StringComparison.Ordinal);
        int column = 0;
        Rect? ink = null;
        foreach (var run in textLine.TextRuns)
        {
            int runStart = column;
            column += run.Length;
            if (run is not Avalonia.Media.TextFormatting.ShapedTextRun shaped || column <= start || runStart >= start + needle.Length) continue;
            var glyphRun = shaped.GlyphRun;
            var r = glyphRun.InkBounds.Translate(new Vector(0, textLine.Baseline - glyphRun.BaselineOrigin.Y));
            ink = ink is { } i ? i.Union(r) : r;
        }
        Assert.NotNull(ink);
        Assert.InRange(orange.Top, ink.Value.Top - 1.5, ink.Value.Top + 1.5);
        Assert.InRange(orange.Bottom, ink.Value.Bottom - 1.5, ink.Value.Bottom + 1.5);
        // 选中背景（整行高）上下都比橙色多出一些
        Assert.True(orange.Top > 0.5 && orange.Bottom < view.DefaultLineHeight - 0.5, $"橙色 {orange}，行高 {view.DefaultLineHeight}");
    }
}
