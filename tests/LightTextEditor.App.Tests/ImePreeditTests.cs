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

        // 选词提交：组字文字消失，输入法提交的文字插入文档
        client.SetPreeditText(null);
        window.KeyTextInput("看不到");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("你好看不到世界", tab.Doc.Document.Text);
        Assert.Empty(area.TextView.GetVisualLine(1)!.Elements.OfType<EditorPreedit.PreeditElement>());
        Assert.Null(client.PreeditText);
    }
}
