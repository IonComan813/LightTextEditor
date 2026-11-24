using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

/// <summary>超长的行（如压缩成一行的 JSON）只排版光标附近的一段；编辑、移动光标、保存都照常。</summary>
public class LongLineTests
{
    private const int LineLength = 2_000_000;

    private static (MainWindow Window, string Library) CreateWindow(bool wordWrap = true)
    {
        var library = Path.Combine(TestAppBuilder.Root, "long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, WordWrap = wordWrap };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    /// <summary>一行很长的有效 JSON（没有换行），长度正好是 <paramref name="length"/>（最后用一个字符串元素补齐）。</summary>
    internal static string LongJson(int length)
    {
        var sb = new StringBuilder(length + 64).Append('[');
        for (int i = 0; ; i++)
        {
            var item = $"{{\"id\":{i},\"name\":\"user {i}\",\"tags\":[\"a\",\"b\"]}},";
            if (sb.Length + item.Length + 3 > length) break;
            sb.Append(item);
        }
        int padding = length - sb.Length - 3; // 引号两个 + ']'
        return sb.Append('"').Append('x', padding).Append("\"]").ToString();
    }

    private static async Task<DocumentTab> OpenAsync(MainWindow window, string library, string text, string ext = ".json")
    {
        var path = Path.Combine(library, "big" + ext);
        await File.WriteAllTextAsync(path, text);
        await window.OpenFilesAsync([path]);
        Render();
        return window.ActiveTab!;
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static VisualLine VisualLineOf(TextEditor editor, int lineNumber = 1)
    {
        var view = editor.TextArea.TextView;
        return view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(lineNumber));
    }

    private static (int Start, int End) WindowOf(TextEditor editor, int lineNumber = 1) =>
        LongLineWindow.WindowOf(editor.TextArea, editor.Document.GetLineByNumber(lineNumber))
        ?? throw new InvalidOperationException("这一行没有按窗口显示");

    /// <summary>光标的位置以 AvaloniaEdit 校验之后为准（校验时落在折叠处里的光标会被挪到边上）。</summary>
    private static int ValidatedCaretOffset(TextEditor editor)
    {
        var position = editor.TextArea.Caret.Position;
        return editor.Document.GetOffset(position.Location);
    }

    private static void Press(MainWindow window, PhysicalKey key, int times = 1, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        for (int i = 0; i < times; i++)
        {
            window.KeyPressQwerty(key, modifiers);
            window.KeyReleaseQwerty(key, modifiers);
            Render();
        }
    }

    [AvaloniaFact]
    public async Task LongLine_IsLaidOutOnlyAroundTheCaret()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength));
        var visualLine = VisualLineOf(tab.Editor);

        var (start, end) = WindowOf(tab.Editor);
        Assert.Equal(0, start);
        Assert.InRange(end, LongLineWindow.Threshold, 200_000);
        Assert.True(visualLine.VisualLength < 200_000, $"显示了 {visualLine.VisualLength} 列");
        var tail = Assert.IsType<LongLineWindow.CollapsedText>(visualLine.Elements[^1]);
        Assert.Equal(LineLength - end, tail.DocumentLength);
        Assert.Equal(LineLength, tab.Doc.Document.TextLength); // 文档内容不变
        Assert.False(tab.Doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task ShortLines_AreNotTouched()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LongLineWindow.Threshold) + "\n" + LongJson(3000));
        Assert.Null(LongLineWindow.WindowOf(tab.Editor.TextArea, tab.Editor.Document.GetLineByNumber(1)));
        Assert.DoesNotContain(VisualLineOf(tab.Editor).Elements, e => e is LongLineWindow.CollapsedText);
        Assert.True(tab.Highlighting!.IsInstalled);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JumpingIntoTheMiddle_ShowsThatPart_AndTypingGoesThere(bool wordWrap)
    {
        var (window, library) = CreateWindow(wordWrap);
        var original = LongJson(LineLength);
        var tab = await OpenAsync(window, library, original);
        var editor = tab.Editor;
        editor.TextArea.Focus();

        const int target = 1_234_567;
        editor.CaretOffset = target;
        editor.TextArea.Caret.BringCaretToView();
        Render();
        var (start, end) = WindowOf(editor);
        Assert.InRange(target, start + 1, end - 1);
        Assert.Equal(target, ValidatedCaretOffset(editor));
        var visualLine = VisualLineOf(editor);
        Assert.IsType<LongLineWindow.CollapsedText>(visualLine.Elements[0]);
        Assert.IsType<LongLineWindow.CollapsedText>(visualLine.Elements[^1]);

        window.KeyTextInput("XYZ");
        Render();
        Assert.Equal("XYZ", editor.Document.GetText(target, 3));
        Assert.Equal(target + 3, ValidatedCaretOffset(editor));

        // 保存的是完整的一行
        tab.Doc.SaveTo(tab.Doc.FilePath!);
        Assert.Equal(original.Insert(target, "XYZ"), await File.ReadAllTextAsync(tab.Doc.FilePath!));
    }

    [AvaloniaFact]
    public async Task ArrowKeys_CrossTheWindowEdge_WithoutJumping_AndTheTextStaysInPlace()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength));
        var editor = tab.Editor;
        var view = editor.TextArea.TextView;
        editor.TextArea.Focus();
        editor.CaretOffset = 1_000_000; // 先跳到中间：窗口按编辑器现在的大小重新取
        editor.TextArea.Caret.BringCaretToView();
        Render();
        var (start, end) = WindowOf(editor);
        int margin = (end - start) / 4; // 光标离窗口末端不足这么远时重新取窗口

        // 从窗口里面出发（还不用重新取），一直往右走，越过要重新取窗口的位置
        int from = end - margin - 40;
        editor.CaretOffset = from;
        editor.TextArea.Caret.BringCaretToView();
        Render();
        Assert.Equal((start, end), WindowOf(editor));
        double ScreenY() => editor.TextArea.Caret.CalculateCaretRectangle().Y - view.ScrollOffset.Y;
        double y = ScreenY();
        Press(window, PhysicalKey.ArrowRight, 80);

        Assert.Equal(from + 80, ValidatedCaretOffset(editor));
        var (newStart, newEnd) = WindowOf(editor);
        Assert.True(newStart > 0, "窗口应当已经往后移");
        Assert.InRange(from + 80, newStart + margin, newEnd - margin);
        // 窗口移动时光标在屏幕上的位置不变（允许换行位置变化带来的一两行误差）
        Assert.InRange(ScreenY(), y - 3 * view.DefaultLineHeight, y + 3 * view.DefaultLineHeight);
        Assert.InRange(ScreenY(), 0, view.Bounds.Height);
    }

    [AvaloniaFact]
    public async Task PageDown_AndPageUp_WalkThroughTheLine()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength));
        var editor = tab.Editor;
        editor.TextArea.Focus();
        editor.CaretOffset = 0;
        Render();

        int previous = 0;
        for (int i = 0; i < 25; i++)
        {
            Press(window, PhysicalKey.PageDown);
            int offset = ValidatedCaretOffset(editor);
            Assert.InRange(offset, previous + 1, previous + 20_000); // 每次大约一屏，不会一下子跳到行尾
            var (start, end) = WindowOf(editor);
            Assert.InRange(offset, start, end);
            previous = offset;
        }
        for (int i = 0; i < 25; i++)
        {
            Press(window, PhysicalKey.PageUp);
            int offset = ValidatedCaretOffset(editor);
            Assert.InRange(offset, Math.Max(0, previous - 20_000), Math.Max(0, previous - 1));
            previous = offset;
        }
    }

    [AvaloniaFact]
    public async Task Search_FindsTextInTheCollapsedPart_AndShowsIt()
    {
        var (window, library) = CreateWindow();
        var text = LongJson(LineLength);
        const string needle = "\"user 40000\"";
        int expected = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(expected > 1_000_000);
        var tab = await OpenAsync(window, library, text);
        var editor = tab.Editor;
        Assert.True(WindowOf(editor).End < expected); // 一开始在折叠的部分里

        var panel = editor.SearchPanel!;
        panel.SearchPattern = needle;
        panel.Open();
        Render();
        panel.FindNext();
        Render();

        Assert.Equal(needle, editor.SelectedText);
        Assert.Equal(expected, editor.SelectionStart);
        Assert.Equal(expected + needle.Length, ValidatedCaretOffset(editor));
        var (start, end) = WindowOf(editor);
        Assert.InRange(expected, start, end - needle.Length);
    }

    [AvaloniaFact]
    public async Task CtrlEnd_GoesToTheEndOfTheLine()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength));
        tab.Editor.TextArea.Focus();
        Press(window, PhysicalKey.End, modifiers: RawInputModifiers.Control);
        Assert.Equal(LineLength, ValidatedCaretOffset(tab.Editor));
        Assert.Equal(LineLength, WindowOf(tab.Editor).End);
        Press(window, PhysicalKey.Home, modifiers: RawInputModifiers.Control);
        Assert.Equal(0, ValidatedCaretOffset(tab.Editor));
        Assert.Equal(0, WindowOf(tab.Editor).Start);
    }

    [AvaloniaFact]
    public async Task ClickingTheCollapsedTail_ShowsWhatFollows()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength));
        var editor = tab.Editor;
        var view = editor.TextArea.TextView;
        var (_, end) = WindowOf(editor);

        // 滚到折叠处，点它
        var visualLine = VisualLineOf(editor);
        var tail = visualLine.Elements.OfType<LongLineWindow.CollapsedText>().Single();
        var top = visualLine.GetVisualPosition(tail.VisualColumn, VisualYPosition.LineTop);
        view.MakeVisible(new Rect(top, new Size(10, view.DefaultLineHeight * 3)));
        Render();
        visualLine = VisualLineOf(editor);
        var middle = visualLine.GetVisualPosition(tail.VisualColumn, VisualYPosition.LineMiddle) + new Vector(20, 0) - view.ScrollOffset;
        var point = view.TranslatePoint(middle, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Render();

        Assert.Equal(end, ValidatedCaretOffset(editor)); // 光标在原来显示的末尾，接着往下显示
        var (newStart, newEnd) = WindowOf(editor);
        Assert.InRange(end, newStart + 1, newEnd - 1);
        Assert.Equal(0, editor.SelectionLength);
    }

    [AvaloniaFact]
    public async Task DeletingMostOfTheLine_ElsewhereThanTheCaret_StillShowsText()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(LineLength) + "\nsecond line");
        var editor = tab.Editor;
        editor.CaretOffset = 1_500_000;
        editor.TextArea.Caret.BringCaretToView();
        Render();
        Assert.True(WindowOf(editor).Start > 1_000_000);

        // 光标到第二行，再删掉第一行的前 160 万个字符：原来显示的那一段已经不在了
        editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset;
        editor.Document.Remove(0, 1_600_000);
        Render();
        var visualLine = VisualLineOf(editor);
        var shown = visualLine.Elements.OfType<VisualLineText>().Sum(e => e.DocumentLength);
        Assert.True(shown >= LongLineWindow.Threshold, $"只显示了 {shown} 个字符");
        Assert.Equal(LineLength - 1_600_000, WindowOf(editor).End);
    }

    [AvaloniaFact]
    public async Task Highlighting_IsSuspendedForLongLines_AndResumesWhenTheyAreGone()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, LongJson(200_000));
        var highlighting = tab.Highlighting!;
        Assert.Equal("source.json", highlighting.Scope);
        Assert.True(highlighting.IsSuspendedForLongLines);
        Assert.False(highlighting.IsInstalled);

        // 整篇替换成格式化后的 JSON：恢复高亮
        var document = tab.Editor.Document;
        document.Replace(0, document.TextLength, LongJson(200_000).Replace(",{", ",\n{"));
        Assert.False(highlighting.IsSuspendedForLongLines);
        Assert.True(highlighting.IsInstalled);

        // 粘贴了一大段没有换行的文字：暂停高亮
        document.Insert(document.TextLength, "\n" + LongJson(50_000));
        Assert.True(highlighting.IsSuspendedForLongLines);
        Assert.False(highlighting.IsInstalled);
    }

    [AvaloniaFact]
    public async Task ImeSurroundingText_IsLimitedOnLongLines()
    {
        var (window, library) = CreateWindow();
        var text = LongJson(LineLength);
        var tab = await OpenAsync(window, library, text);
        var area = tab.Editor.TextArea;
        area.Focus();
        tab.Editor.CaretOffset = 500_000;
        Render();
        var client = EditorPreedit.ClientOf(area)!;
        Assert.Equal(text.Substring(500_000 - 1000, 2001), client.SurroundingText);
        // 选区位置相对于返回的这段文字（与短行时的约定相同：用的是光标的列号）
        Assert.Equal(500_000 + 1 - (500_000 - 1000), client.Selection.Start);
    }
}

/// <summary>超长行的截图（人工检查折叠提示的样子）。只有设置了 LTE_SCREENSHOT_DIR 时才写文件。</summary>
public class LongLineShots
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LongLine(bool wordWrap)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var library = Path.Combine(TestAppBuilder.Root, "longshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, WordWrap = wordWrap };
        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "data.json");
        File.WriteAllText(path, LongLineTests.LongJson(2_000_000) + "\n");
        if (!wordWrap)
        {
            // 压缩过的 JavaScript：提示条；“编辑”菜单
            var js = Path.Combine(library, "bundle.min.js");
            File.WriteAllText(js, string.Concat(Enumerable.Repeat("!function(e){var t={a:1};function r(){return t}e.r=r}(window);", 300)));
            await window.OpenFilesAsync([js]);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "compressed-js-opened.png"));
            var edit = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.MenuItem>()
                .Single(m => m.Header as string == "编辑(_E)");
            edit.Open();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "edit-menu.png"));
            edit.Close();
            window.ActiveTab!.Doc.Notice!.Actions.Single().Execute();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "compressed-js-expanded.png"));
        }
        await window.OpenFilesAsync([path]);
        var editor = window.ActiveTab!.Editor;
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"long-line-{(wordWrap ? "wrap" : "nowrap")}-opened.png"));
        window.ActiveTab.Doc.Notice = null;
        editor.CaretOffset = 1_000_000;
        editor.TextArea.Caret.BringCaretToView();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        // 滚到这一段的开头，看前面的折叠提示
        var view = editor.TextArea.TextView;
        var line = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        view.MakeVisible(new Rect(line.GetVisualPosition(0, AvaloniaEdit.Rendering.VisualYPosition.LineTop), new Size(1, 1)));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"long-line-{(wordWrap ? "wrap" : "nowrap")}-start.png"));
        // 末尾的折叠提示
        line = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        view.MakeVisible(new Rect(line.GetVisualPosition(line.VisualLength, AvaloniaEdit.Rendering.VisualYPosition.LineBottom), new Size(1, 40)));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"long-line-{(wordWrap ? "wrap" : "nowrap")}-end.png"));
    }
}
