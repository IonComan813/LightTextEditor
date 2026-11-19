using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.ViewModels;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

/// <summary>编码"待确认"提示条、编码记录、转换为 UTF-8、无法识别字节的保护。</summary>
public class EncodingTests
{
    static EncodingTests() => TextEncoding.EnsureCodePagesRegistered();

    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    private static string WriteFile(string library, string name, byte[] bytes)
    {
        var path = Path.Combine(library, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static NoticeAction Action(DocumentViewModel doc, string label) =>
        Assert.Single(doc.Notice!.Actions, a => a.Label == label);

    private static FAContentDialog? FindDialog(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<FAContentDialog>().SingleOrDefault();
    }

    private const int ReopenCount = 10;

    private const string LongGbk = "这是一个用于测试编码检测的简体中文段落。它包含常用的汉字、标点符号，以及数字和英文。";

    [AvaloniaFact]
    public async Task UncertainEncoding_ShowsConfirmationBar_WithThreeActions()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "short.txt", Encoding.GetEncoding(936).GetBytes("你好世界"));

        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;
        Assert.Equal("请确认编码", doc.Notice?.Title);
        Assert.Contains("GB18030（简体中文）", doc.Notice!.Message);
        Assert.Equal(["显示正常", "换一个编码", "转换为 UTF-8"], doc.Notice.Actions.Select(a => a.Label));

        var choices = Action(doc, "换一个编码").Children!;
        Assert.Contains(choices, c => c.Label.StartsWith("Big5"));
        Assert.StartsWith("GB18030", Assert.Single(choices, c => c.IsChecked).Label);

        // 提示条上的下拉按钮
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Border>("NoticeHost")!.IsVisible);
        var bar = window.FindControl<FAInfoBar>("NoticeBar")!;
        var dropDown = Assert.IsType<DropDownButton>(Assert.IsType<StackPanel>(bar.Content).Children[1]);
        Assert.Equal("换一个编码", dropDown.Content);
        Assert.Equal(ReopenCount, ((MenuFlyout)dropDown.Flyout!).Items.Count);
    }

    [AvaloniaFact]
    public async Task ConfirmingEncoding_IsRemembered_ForNextOpen()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "confirm.txt", Encoding.GetEncoding(936).GetBytes("中文"));

        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Action(tab.Doc, "显示正常").Execute();
        Assert.True(tab.Doc.Encoding.IsCertain);
        Assert.Null(tab.Doc.Notice);
        Assert.False(tab.Doc.IsDirty); // 确认编码不改动文件
        Assert.Equal(54936, App.EncodingMemory.Get(path)?.CodePage);

        Assert.True(await window.CloseTabAsync(tab));
        await window.OpenFilesAsync([path]);
        var reopened = window.ActiveTab!.Doc;
        Assert.True(reopened.Encoding.IsCertain);
        Assert.Null(reopened.Notice);
        Assert.Equal("GB18030", window.FindControl<Button>("EncodingButton")!.Content);
    }

    [AvaloniaFact]
    public async Task ChoosingAnotherEncoding_Reopens_AndIsRemembered()
    {
        var (window, library) = CreateWindow();
        // 太短，按结构推测会猜成 GB18030
        var path = WriteFile(library, "big5.txt", Encoding.GetEncoding(950).GetBytes("中文"));

        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;
        Assert.NotEqual("中文", doc.Document.Text);

        Assert.Single(Action(doc, "换一个编码").Children!, c => c.Label.StartsWith("Big5")).Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("中文", doc.Document.Text);
        Assert.Equal(950, doc.Encoding.CodePage);
        Assert.True(doc.Encoding.IsCertain);
        Assert.Null(doc.Notice);
        Assert.Equal(950, App.EncodingMemory.Get(path)?.CodePage);
    }

    [AvaloniaFact]
    public async Task ConvertToUtf8_RewritesFile_WithBomForTxt_AndForgetsMemory()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "convert.txt", Encoding.GetEncoding(936).GetBytes(LongGbk));
        App.EncodingMemory.Remember(path, TextEncoding.Gb18030(true));

        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.True(tab.Doc.Encoding.IsCertain); // 编码记录 → 无需确认，不弹对话框

        Assert.True(await window.ConvertToUtf8Async(tab));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(LongGbk, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal("UTF-8 with BOM", tab.Doc.Encoding.DisplayName);
        Assert.False(tab.Doc.IsDirty);
        Assert.Null(App.EncodingMemory.Get(path));
    }

    [AvaloniaFact]
    public async Task ConvertToUtf8_OnUncertainEncoding_AsksFirst()
    {
        var (window, library) = CreateWindow();
        var original = Encoding.GetEncoding(936).GetBytes("你好世界");
        var path = WriteFile(library, "ask.cs", original);

        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        var task = window.ConvertToUtf8Async(tab);
        var dialog = FindDialog(window)!;
        Assert.Equal("转换为 UTF-8", dialog.Title);
        dialog.Hide(FAContentDialogResult.None);
        Assert.False(await task);
        Assert.Equal(original, File.ReadAllBytes(path));

        task = window.ConvertToUtf8Async(tab);
        FindDialog(window)!.Hide(FAContentDialogResult.Primary);
        Assert.True(await task);
        Assert.Equal(Encoding.UTF8.GetBytes("你好世界"), File.ReadAllBytes(path)); // 代码文件不带 BOM
    }

    [AvaloniaFact]
    public async Task SavingFileWithUndecodableBytes_AsksFirst()
    {
        var (window, library) = CreateWindow();
        byte[] original = [0xEF, 0xBB, 0xBF, 0x61, 0xFF, 0x62];
        var path = WriteFile(library, "broken.txt", original);

        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.Equal("有无法识别的字节", tab.Doc.Notice?.Title);
        Assert.DoesNotContain(tab.Doc.Notice!.Actions, a => a.Label == "转换为 UTF-8");

        var save = window.SaveActiveAsync();
        var dialog = FindDialog(window)!;
        Assert.Equal("有无法识别的字节", dialog.Title);
        dialog.Hide(FAContentDialogResult.None);
        Assert.False(await save);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task SavingLegacyEncoding_KeepsBytes_AndRemembersEncoding()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "keep.txt", Encoding.GetEncoding(936).GetBytes(LongGbk));
        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.Equal(DetectionMethod.Statistical, tab.Doc.Detection!.Method);

        tab.Editor.Document.Insert(tab.Editor.Document.TextLength, "追加");
        Assert.True(await window.SaveActiveAsync());
        Assert.Equal(Encoding.GetEncoding(936).GetBytes(LongGbk + "追加"), File.ReadAllBytes(path));
        Assert.Equal(54936, App.EncodingMemory.Get(path)?.CodePage);
    }

    [AvaloniaFact]
    public async Task NewFile_UsesEditorConfigCharset()
    {
        var (window, library) = CreateWindow();
        File.WriteAllText(Path.Combine(library, ".editorconfig"), "root = true\n[*.txt]\ncharset = utf-8\n");
        var tab = window.ActiveTab!;
        tab.Editor.Document.Text = "中文";

        var path = Path.Combine(library, "new.txt");
        Assert.True(await window.SaveAsToAsync(tab, path));
        Assert.Equal(Encoding.UTF8.GetBytes("中文"), File.ReadAllBytes(path)); // editorconfig：不带 BOM（默认 .txt 会带）
    }
}
