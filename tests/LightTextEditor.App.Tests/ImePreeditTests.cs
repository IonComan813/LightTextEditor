using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class ImePreeditTests
{
    [AvaloniaFact]
    public async Task Preedit_IsShownAtCaret_WithoutChangingTheDocument()
    {
        var library = Path.Combine(TestAppBuilder.Root, "ime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "你好世界");
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        var tab = window.ActiveTab!;
        var area = tab.Editor.TextArea;
        area.Focus();
        tab.Editor.CaretOffset = 2;
        Dispatcher.UIThread.RunJobs();

        // 输入法向编辑器要客户端时，拿到的是支持组字的客户端（不是 AvaloniaEdit 自带的）
        var request = (TextInputMethodClientRequestedEventArgs)Activator.CreateInstance(typeof(TextInputMethodClientRequestedEventArgs), nonPublic: true)!;
        request.RoutedEvent = InputElement.TextInputMethodClientRequestedEvent;
        area.RaiseEvent(request);
        var client = Assert.IsType<EditorPreedit.PreeditClient>(request.Client);
        Assert.Same(EditorPreedit.ClientOf(area), client);
        Assert.True(client.SupportsPreedit);

        // 组字：拼音显示在光标处，后面的文字往后让，文档不变
        client.SetPreeditText("kan'bu'dao", 10);
        Dispatcher.UIThread.RunJobs();
        var line = area.TextView.GetVisualLine(1)!;
        var element = Assert.Single(line.Elements.OfType<EditorPreedit.PreeditElement>());
        Assert.Equal("kan'bu'dao", element.Text);
        Assert.Equal(2, element.VisualColumn);
        Assert.Equal(3 + 10, line.GetVisualColumn(3)); // “世”之后
        Assert.Equal("你好世界", tab.Doc.Document.Text);
        Assert.False(tab.Doc.IsDirty);
        Assert.Equal(4, window.ViewModel.CharacterCount);
        Assert.Equal(2, tab.Editor.CaretOffset);

        // 拼音更新
        client.SetPreeditText("kan'bu'daoa", 11);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("kan'bu'daoa", Assert.Single(area.TextView.GetVisualLine(1)!.Elements.OfType<EditorPreedit.PreeditElement>()).Text);
        Assert.Equal(11, client.PreeditCursor);

        // 选词提交：组字文字消失，输入法提交的文字插入文档
        client.SetPreeditText(null);
        window.KeyTextInput("看不到");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("你好看不到世界", tab.Doc.Document.Text);
        Assert.Empty(area.TextView.GetVisualLine(1)!.Elements.OfType<EditorPreedit.PreeditElement>());
        Assert.Null(client.PreeditText);
    }

    [AvaloniaFact]
    public async Task PreeditCursor_UsesThePositionReportedByIme()
    {
        var library = Path.Combine(TestAppBuilder.Root, "ime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "");
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        var client = EditorPreedit.ClientOf(window.ActiveTab!.Editor.TextArea)!;

        // Windows 上微软拼音实际报告的（见 ime-debug.log）：每次都带光标位置
        client.SetPreeditText("d", 1);
        Assert.Equal(1, client.PreeditCursor);
        client.SetPreeditText("d's", 3);
        Assert.Equal(3, client.PreeditCursor);
        // 用方向键在拼音里移动
        client.SetPreeditText("d's", 1);
        Assert.Equal(1, client.PreeditCursor);
        // 没有报告位置：在末尾；超出范围：限制在组字文字之内
        client.SetPreeditText("d's's", null);
        Assert.Equal(5, client.PreeditCursor);
        client.SetPreeditText("d's", 9);
        Assert.Equal(3, client.PreeditCursor);
    }

    [AvaloniaFact]
    public async Task DebugLog_RecordsPreeditCalls_WhenEnabledInSettings()
    {
        var library = Path.Combine(TestAppBuilder.Root, "ime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, ImeDebugLog = true };
        try
        {
            var window = new MainWindow { Width = 1000, Height = 600 };
            window.Initialize([]);
            window.Show();
            var path = Path.Combine(library, "a.txt");
            File.WriteAllText(path, "");
            await window.OpenFilesAsync([path]);
            Dispatcher.UIThread.RunJobs();
            var area = window.ActiveTab!.Editor.TextArea;
            area.Focus();
            var client = EditorPreedit.ClientOf(area)!;
            client.SetPreeditText("g", 1);
            client.SetPreeditText("gu", 2);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame(); // 画一帧：记录光标画在哪里
            client.SetPreeditText(null);
            window.KeyTextInput("谷");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            ImeDebugLog.Stop();
        }

        var log = File.ReadAllText(ImeDebugLog.FilePath);
        Assert.Contains("[client] SetPreeditText(\"gu\", 2) 上次=\"g\" → 光标 2", log);
        Assert.Contains("[draw] 组字 \"gu\" 光标 2：画在第 2 列", log);
        Assert.Contains("→ 清除组字", log);
        Assert.Contains("[input] TextInput \"谷\"", log);
    }

    [AvaloniaFact]
    public async Task PreeditCaret_IsRedrawn_AfterEachKeystroke()
    {
        var library = Path.Combine(TestAppBuilder.Root, "ime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 600 };
        window.Initialize([]);
        window.Show();
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "你好");
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        var area = window.ActiveTab!.Editor.TextArea;
        area.Focus();
        window.ActiveTab.Editor.CaretOffset = 2;
        Dispatcher.UIThread.RunJobs();
        var client = EditorPreedit.ClientOf(area)!;

        // 每次按键之后都画一帧（和真实程序一样），光标要画在新的位置，不能停在第一次画的地方
        void KeyAndRender(string text, int cursor)
        {
            client.SetPreeditText(text, cursor);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }
        KeyAndRender("d", 1);
        Assert.Equal(2 + 1, client.LastDrawnCaretColumn);
        KeyAndRender("d's", 3);
        Assert.Equal(2 + 3, client.LastDrawnCaretColumn);
        KeyAndRender("d's's", 5);
        Assert.Equal(2 + 5, client.LastDrawnCaretColumn);

        // 选词后组字光标不再画
        client.SetPreeditText(null);
        window.KeyTextInput("打算说");
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Assert.Null(client.LastDrawnCaretColumn);
    }
}
