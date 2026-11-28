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

/// <summary>打开被压缩为单个超长行的文件时询问是否还原为多行；“编辑”菜单里的展开为多行 / 压缩为单行。</summary>
public class ReformatTests
{
    private const string MinifiedJs =
        "!function(e){\"use strict\";var t={a:1,b:\"x;{}\"},n=/[;{}]/g;function r(){for(var i=0;i<3;i++){if(i)continue;else break}return t}e.r=r}(window);";

    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, TabSize = 2 };
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

    private static MenuItem EditMenuItem(MainWindow window, string header) =>
        window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == header);

    private static void ClickMenu(MainWindow window, string header)
    {
        EditMenuItem(window, header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static bool HasLongLine(DocumentTab tab) =>
        tab.Editor.Document.Lines.Any(l => l.Length > Services.LongLineWindow.Threshold);

    /// <summary>重复到超过长行的长度，仍是一行。</summary>
    private static string Repeat(string unit, int minLength) =>
        string.Concat(Enumerable.Repeat(unit, minLength / unit.Length + 1));

    [AvaloniaFact]
    public async Task MinifiedJson_AsksToExpand_AndTheCheckmarkExpandsIt()
    {
        var (window, library) = CreateWindow();
        var json = LongLineTests.LongJson(300_000);
        var tab = await OpenAsync(window, library, "data.json", json + "\n");
        var doc = tab.Doc;

        Assert.Equal("当前文件被压缩为单个超长行", doc.Notice?.Title);
        Assert.StartsWith("已暂时关闭语法高亮。是否还原为多行结构？", doc.Notice!.Message);
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
        Assert.Equal(json, JsonText.Minify(text));

        // 一次就能撤销
        tab.Editor.Undo();
        Assert.Equal(json + "\n", tab.Editor.Document.Text);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData("bundle.min.js", "js", true)]
    [InlineData("site.min.css", "css", true)]
    public async Task OtherCompressedFiles_AreAlsoRecognized(string name, string kind, bool highlighted)
    {
        var (window, library) = CreateWindow();
        var text = kind switch
        {
            "js" => Repeat(MinifiedJs, 50_000),
            _ => Repeat("a.b{color:red;background:url(data:x;y)}@media (max-width:1px){c{d:e}}", 50_000),
        };
        var tab = await OpenAsync(window, library, name, text);
        Assert.Equal("当前文件被压缩为单个超长行", tab.Doc.Notice?.Title);
        Assert.Equal(highlighted, tab.Doc.Notice!.Message.Contains("已暂时关闭语法高亮"));

        tab.Doc.Notice.Actions.Single().Execute();
        Assert.False(HasLongLine(tab));
        Assert.Equal(highlighted, tab.Highlighting!.IsInstalled);
    }

    [AvaloniaTheory]
    [InlineData("pretty.json")] // 已经是多行
    [InlineData("long.log")]    // 很长的一行，但不是能展开的文件
    [InlineData("dump.txt")]    // 其他后缀：内容是压缩的 JSON 也不支持、不提示
    [InlineData("app.js")]      // 一行很长的 JavaScript，但含有 JSX：不能可靠地展开，不提示
    public async Task OtherFiles_DoNotAsk(string name)
    {
        var (window, library) = CreateWindow();
        var text = name switch
        {
            "pretty.json" => JsonText.Format(LongLineTests.LongJson(50_000), "  ", "\n"),
            "long.log" => Repeat("2026-11-24 12:00:00 INFO something happened; ", 50_000),
            "dump.txt" => LongLineTests.LongJson(50_000),
            _ => Repeat("x=<div>a;b</div>;", 50_000),
        };
        var tab = await OpenAsync(window, library, name, text);
        Assert.Null(tab.Doc.Notice);
    }

    [AvaloniaFact]
    public void EditMenu_HasPlainHeaders()
    {
        var (window, _) = CreateWindow();
        Assert.NotNull(EditMenuItem(window, "展开为多行"));
        Assert.NotNull(EditMenuItem(window, "压缩为单行"));
    }

    [AvaloniaFact]
    public async Task EditMenu_CompactsAndExpands_TheWholeFile_EvenWithASelection()
    {
        var (window, library) = CreateWindow();
        const string pretty = "{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": \"x y\"\n}\n";
        var tab = await OpenAsync(window, library, "small.json", pretty);
        Assert.Null(tab.Doc.Notice);
        tab.Editor.Select(4, 10); // 有选中的文字也处理整个文件

        ClickMenu(window, "压缩为单行");
        Assert.Equal("{\"a\":[1,2],\"b\":\"x y\"}\n", tab.Editor.Document.Text);
        Assert.Equal("已压缩为单行", tab.Doc.Notice?.Title);

        ClickMenu(window, "展开为多行");
        Assert.Equal(pretty, tab.Editor.Document.Text);
        Assert.False(tab.Doc.IsDirty); // 和打开时一样

        // 压缩成超长的一行：语法高亮自动暂停；展开后恢复
        var big = JsonText.Format(LongLineTests.LongJson(50_000), "  ", "\n");
        tab.Editor.Document.Text = big;
        Assert.True(tab.Highlighting!.IsInstalled);
        ClickMenu(window, "压缩为单行");
        Assert.True(HasLongLine(tab));
        Assert.True(tab.Highlighting.IsSuspendedForLongLines);
        ClickMenu(window, "展开为多行");
        Assert.Equal(big, tab.Editor.Document.Text);
        Assert.True(tab.Highlighting.IsInstalled);
    }

    [AvaloniaFact]
    public async Task EditMenu_JavaScript_KeepsLineBreaksThatMatter()
    {
        var (window, library) = CreateWindow();
        const string src = "const a = 1\nconst b = {\n  x: 1\n}\nfoo()\n";
        var tab = await OpenAsync(window, library, "app.js", src);

        ClickMenu(window, "压缩为单行");
        Assert.Equal("const a = 1\nconst b ={x: 1};foo()\n", tab.Editor.Document.Text);
        Assert.Equal("已压缩", tab.Doc.Notice?.Title);
        Assert.Contains("有 1 处换行会影响代码的含义", tab.Doc.Notice!.Message);
    }

    [AvaloniaFact]
    public async Task EditMenu_LeavesInvalidJsonAlone()
    {
        var (window, library) = CreateWindow();
        const string broken = "{\n  \"a\": 1,\n  \"b\": \n}";
        var tab = await OpenAsync(window, library, "broken.json", broken);

        ClickMenu(window, "压缩为单行");
        Assert.Equal(broken, tab.Editor.Document.Text);
        Assert.Equal("无法压缩这个 JSON 文件，没有改动", tab.Doc.Notice?.Title);
        Assert.Contains("第 4 行", tab.Doc.Notice!.Message);
        Assert.Equal(NoticeSeverity.Warning, tab.Doc.Notice.Severity);
        Assert.False(tab.Doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task EditMenu_ExplainsUnsupportedFiles()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, library, "page.html", "<p>a</p>\n<p>b</p>");
        ClickMenu(window, "展开为多行");
        Assert.Equal("<p>a</p>\n<p>b</p>", tab.Editor.Document.Text);
        Assert.Equal("无法展开：不支持这种文件", tab.Doc.Notice?.Title);

        // .txt 里即使是 JSON 也不压缩
        var txt = await OpenAsync(window, library, "data.txt", "{\n  \"a\": 1\n}");
        ClickMenu(window, "压缩为单行");
        Assert.Equal("{\n  \"a\": 1\n}", txt.Editor.Document.Text);
        Assert.Equal("无法压缩：不支持这种文件", txt.Doc.Notice?.Title);
    }
}
