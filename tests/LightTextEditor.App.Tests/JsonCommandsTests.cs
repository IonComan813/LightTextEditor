using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.ViewModels;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

/// <summary>打开压缩成一行的 JSON 时询问是否还原为多行；“编辑”菜单里的展开 / 压缩 JSON。</summary>
public class JsonCommandsTests
{
    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false, TabSize = 2 };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    private static async Task<DocumentTab> OpenAsync(MainWindow window, string library, string name, string text)
    {
        var path = Path.Combine(library, name);
        await File.WriteAllTextAsync(path, text);
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        return window.ActiveTab!;
    }

    private static void ClickMenu(MainWindow window, string headerStart)
    {
        var item = window.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => m.Header is string h && h.StartsWith(headerStart, StringComparison.Ordinal));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static bool HasLongLine(DocumentTab tab) =>
        tab.Editor.Document.Lines.Any(l => l.Length > Services.LongLineWindow.Threshold);

    [AvaloniaFact]
    public async Task MinifiedJson_AsksToExpand_AndTheCheckmarkExpandsIt()
    {
        var (window, library) = CreateWindow();
        var json = LongLineTests.LongJson(300_000);
        var tab = await OpenAsync(window, library, "data.json", json + "\n");
        var doc = tab.Doc;

        Assert.Equal("当前文件是单行压缩的 JSON", doc.Notice?.Title);
        Assert.Contains("已暂时关闭语法高亮", doc.Notice!.Message);
        Assert.Contains("是否还原为多行结构", doc.Notice.Message);
        var action = Assert.Single(doc.Notice.Actions);
        Assert.Equal(NoticeIcon.Checkmark, action.Icon);
        Assert.False(tab.Highlighting!.IsInstalled);

        // 提示条：✓ 按钮 + 关闭按钮（×）
        var bar = window.FindControl<FAInfoBar>("NoticeBar")!;
        Assert.True(bar.IsOpen);
        Assert.True(bar.IsClosable);
        var check = Assert.IsType<Button>(Assert.IsType<StackPanel>(bar.Content).Children.Single());
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var text = tab.Editor.Document.Text;
        Assert.StartsWith("[\n  {\n    \"id\": 0,\n    \"name\": \"user 0\",", text);
        Assert.EndsWith("\n]\n", text); // 文件末尾的换行保留
        Assert.False(HasLongLine(tab));
        Assert.True(tab.Highlighting.IsInstalled); // 高亮恢复
        Assert.True(doc.IsDirty);
        Assert.Equal("已展开为多行", doc.Notice?.Title);
        Assert.Equal(json + "\n", JsonText.Minify(text) + "\n");

        // 一次就能撤销
        tab.Editor.Undo();
        Assert.Equal(json + "\n", tab.Editor.Document.Text);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task MinifiedJson_WithAnotherExtension_IsAlsoRecognized()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, "dump.txt", LongLineTests.LongJson(50_000));
        Assert.Equal("当前文件是单行压缩的 JSON", tab.Doc.Notice?.Title);
        Assert.DoesNotContain("语法高亮", tab.Doc.Notice!.Message); // .txt 本来就没有高亮
    }

    [AvaloniaTheory]
    [InlineData("pretty.json", false)]  // 已经是多行
    [InlineData("long.log", true)]      // 很长的一行，但不是 JSON
    public async Task OtherFiles_DoNotAsk(string name, bool longLine)
    {
        var (window, library) = CreateWindow();
        var text = longLine
            ? string.Concat(Enumerable.Repeat("2026-11-24 12:00:00 INFO something happened; ", 2000))
            : JsonText.Format(LongLineTests.LongJson(50_000), "  ", "\n");
        var tab = await OpenAsync(window, library, name, text);
        Assert.Null(tab.Doc.Notice);
    }

    [AvaloniaFact]
    public async Task EditMenu_CompactsAndExpands()
    {
        var (window, library) = CreateWindow();
        const string pretty = "{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": \"x y\"\n}\n";
        var tab = await OpenAsync(window, library, "small.json", pretty);
        Assert.Null(tab.Doc.Notice);

        ClickMenu(window, "压缩 JSON");
        Assert.Equal("{\"a\":[1,2],\"b\":\"x y\"}\n", tab.Editor.Document.Text);
        Assert.Equal("已压缩为一行", tab.Doc.Notice?.Title);

        ClickMenu(window, "展开 JSON");
        Assert.Equal(pretty, tab.Editor.Document.Text);
        Assert.False(tab.Doc.IsDirty); // 和打开时一样

        // 压缩成超长的一行：语法高亮自动暂停；展开后恢复
        var big = JsonText.Format(LongLineTests.LongJson(50_000), "  ", "\n");
        tab.Editor.Document.Text = big;
        Assert.True(tab.Highlighting!.IsInstalled);
        ClickMenu(window, "压缩 JSON");
        Assert.True(HasLongLine(tab));
        Assert.True(tab.Highlighting.IsSuspendedForLongLines);
        ClickMenu(window, "展开 JSON");
        Assert.Equal(big, tab.Editor.Document.Text);
        Assert.True(tab.Highlighting.IsInstalled);
    }

    [AvaloniaFact]
    public async Task EditMenu_WorksOnTheSelectionOnly()
    {
        var (window, library) = CreateWindow();
        const string log = "请求：{\"id\":1,\"ok\":true} 完成";
        var tab = await OpenAsync(window, library, "app.log", log);
        int start = log.IndexOf('{');
        tab.Editor.Select(start, log.IndexOf('}') + 1 - start);

        ClickMenu(window, "展开 JSON");
        Assert.Equal("请求：{\n  \"id\": 1,\n  \"ok\": true\n} 完成", tab.Editor.Document.Text);
        Assert.Equal("{\n  \"id\": 1,\n  \"ok\": true\n}", tab.Editor.SelectedText); // 结果仍然选中
    }

    [AvaloniaFact]
    public async Task EditMenu_LeavesInvalidJsonAlone()
    {
        var (window, library) = CreateWindow();
        const string broken = "{\n  \"a\": 1,\n  \"b\": \n}";
        var tab = await OpenAsync(window, library, "broken.json", broken);

        ClickMenu(window, "压缩 JSON");
        Assert.Equal(broken, tab.Editor.Document.Text);
        Assert.Equal("无法压缩：当前内容不是有效的 JSON", tab.Doc.Notice?.Title);
        Assert.Contains("第 4 行", tab.Doc.Notice!.Message);
        Assert.Equal(NoticeSeverity.Warning, tab.Doc.Notice.Severity);
        Assert.False(tab.Doc.IsDirty);
    }
}
