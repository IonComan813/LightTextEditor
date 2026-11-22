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
    public async Task PreeditCursor_FollowsTyping_EvenWhenImeReportsTheWrongPosition()
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

        // Windows 上的微软拼音：每次按键都报告光标位置 1，而且同一次按键会报告两次（第二次文字不变）
        void Key(string text)
        {
            client.SetPreeditText(text, 1);
            client.SetPreeditText(text, 1);
        }
        Key("g");
        Assert.Equal(1, client.PreeditCursor);
        Key("gu");
        Key("gua");
        Key("guang");
        Key("guang'b"); // 输入法自动加上分隔符
        Assert.Equal(7, client.PreeditCursor);
        Key("guang'biao");
        Assert.Equal(10, client.PreeditCursor);
        Key("guang'bia"); // 退格
        Assert.Equal(9, client.PreeditCursor);

        // 报告的位置是 0 或不报告：一样跟着文字走
        client.SetPreeditText("guang'biao", 0);
        Assert.Equal(10, client.PreeditCursor);
        client.SetPreeditText("guang'biaox", null);
        Assert.Equal(11, client.PreeditCursor);

        // 下一次组字重新开始
        client.SetPreeditText(null);
        Key("h");
        Key("ha");
        Assert.Equal(2, client.PreeditCursor);
    }

    [AvaloniaFact]
    public async Task PreeditCursor_MovesWithArrowKeys_WhenImeReportsIt()
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

        client.SetPreeditText("g", 1);
        client.SetPreeditText("guang'b", 7);
        Assert.Equal(7, client.PreeditCursor);
        // 用方向键在拼音里移动：文字不变，报告新的位置
        client.SetPreeditText("guang'b", 5);
        Assert.Equal(5, client.PreeditCursor);
        // 在中间打字：光标跟着往后走；退格：往前走
        client.SetPreeditText("guangx'b", 6);
        Assert.Equal(6, client.PreeditCursor);
        client.SetPreeditText("guang'b", 5);
        Assert.Equal(5, client.PreeditCursor);
        // 移回末尾后再打字：保持在末尾
        client.SetPreeditText("guang'b", 7);
        client.SetPreeditText("guang'bi", 8);
        Assert.Equal(8, client.PreeditCursor);
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
            client.SetPreeditText("gu", 1);
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
        Assert.Contains("[client] SetPreeditText(\"gu\", 1) 上次=\"g\"/报告1 → 光标 2", log);
        Assert.Contains("[draw] 组字 \"gu\" 光标 2：画在第 2 列", log);
        Assert.Contains("→ 清除组字", log);
        Assert.Contains("[input] TextInput \"谷\"", log);
    }
}
