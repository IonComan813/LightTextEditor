using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    /// <summary>提示条上"更换编码重试"下拉按钮（换过编码后它的文字是当前编码名）。</summary>
    private static NoticeAction Switcher(DocumentViewModel doc) => Assert.Single(doc.Notice!.Actions, a => a.Children is not null);

    private static NoticeAction Choice(DocumentViewModel doc, string prefix) =>
        Assert.Single(Switcher(doc).Children!, c => c.Label.StartsWith(prefix));

    private static FAContentDialog? FindDialog(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<FAContentDialog>().SingleOrDefault();
    }

    private const int ReopenCount = 10;

    private const string LongGbk = "这是一个用于测试编码检测的简体中文段落。它包含常用的汉字、标点符号，以及数字和英文。";

    [AvaloniaFact]
    public async Task UncertainEncoding_ShowsPersistentBar_SwitcherThenCheckmark()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "short.txt", Encoding.GetEncoding(936).GetBytes("你好世界"));

        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;
        Assert.Equal("请确认当前文件的编码格式", doc.Notice?.Title);
        Assert.True(doc.Notice!.IsStanding);
        Assert.Contains("GB18030（简体中文）", doc.Notice.Message);
        Assert.Equal(["更换编码重试", "显示正常"], doc.Notice.Actions.Select(a => a.Label));
        Assert.Equal(NoticeIcon.Checkmark, Action(doc, "显示正常").Icon);

        // 检测时考虑过的（UTF-8 校验失败）和打开时采用的编码都是灰色，仍可选择
        var choices = Switcher(doc).Children!;
        Assert.Contains(choices, c => c.Label.StartsWith("Big5") && !c.IsDimmed);
        var current = Assert.Single(choices, c => c.IsChecked);
        Assert.StartsWith("GB18030", current.Label);
        Assert.True(current.IsDimmed);
        Assert.True(Choice(doc, "UTF-8").IsDimmed);

        // 提示条：下拉按钮在左，✓ 按钮（只有图标，提示文字"显示正常"）在右，可以关闭
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Border>("NoticeHost")!.IsVisible);
        var bar = window.FindControl<FAInfoBar>("NoticeBar")!;
        Assert.True(bar.IsClosable);
        var buttons = Assert.IsType<StackPanel>(bar.Content).Children;
        var dropDown = Assert.IsType<DropDownButton>(buttons[0]);
        Assert.Equal("更换编码重试", dropDown.Content);
        Assert.Equal(choices.Count, ((MenuFlyout)dropDown.Flyout!).Items.Count);
        var check = Assert.IsType<Button>(buttons[1]);
        Assert.IsType<FASymbolIcon>(check.Content);
        Assert.Equal("显示正常", ToolTip.GetTip(check));
    }

    [AvaloniaFact]
    public async Task EncodingsConsideredByDetection_AreListedAndDimmed()
    {
        var (window, library) = CreateWindow();
        // 4 字节的 GBK"中文"：统计检测给出 KOI8-R（置信度太低，未采用），最后按结构推测为 GB18030
        var path = WriteFile(library, "tiny.txt", Encoding.GetEncoding(936).GetBytes("中文"));
        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;

        Assert.Equal([65001, 20866, 54936], doc.TriedCodePages);
        var choices = Switcher(doc).Children!;
        Assert.Equal(ReopenCount + 1, choices.Count);
        var koi8 = choices[^1]; // 不在常用列表里的编码追加在后面
        Assert.Equal("KOI8-R（西里尔文）", koi8.Label);
        Assert.True(koi8.IsDimmed);
    }

    [AvaloniaFact]
    public async Task PersistentBar_ComesBack_AfterOtherNotices_AndSaving_UntilCheckmark()
    {
        var (window, library) = CreateWindow();
        var original = Encoding.GetEncoding(936).GetBytes("你好世界");
        var path = WriteFile(library, "stay.txt", original);
        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        var bar = tab.Doc.Notice;

        // 其他提示暂时替换它，关闭后恢复
        tab.Doc.Notice = new DocumentNotice("别的提示", "", NoticeSeverity.Informational, []);
        tab.Doc.Notice = null;
        Assert.Equal("请确认当前文件的编码格式", tab.Doc.Notice?.Title);

        // 保存不算确认：仍按原编码写回，提示条还在，也不记入编码记录
        tab.Editor.Document.Insert(tab.Editor.Document.TextLength, "！");
        Assert.True(await window.SaveActiveAsync());
        Assert.Equal(Encoding.GetEncoding(936).GetBytes("你好世界！"), File.ReadAllBytes(path));
        Assert.False(tab.Doc.Encoding.IsCertain);
        Assert.Equal("请确认当前文件的编码格式", tab.Doc.Notice?.Title);
        Assert.Null(App.EncodingMemory.Get(path));

        // 点 ✓ 后才消失（换成是否转换为 UTF-8 的询问，那个可以关闭）
        Action(tab.Doc, "显示正常").Execute();
        Assert.Equal("编码已确认", tab.Doc.Notice?.Title);
        Assert.False(tab.Doc.Notice!.IsStanding);
        tab.Doc.DismissNotice();
        Assert.Null(tab.Doc.Notice);
    }

    [AvaloniaFact]
    public async Task ClosingBarBeforeConfirming_KeepsItClosed_UntilAnotherEncodingIsTried()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "close.txt", Encoding.GetEncoding(950).GetBytes("中文"));
        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.True(tab.Doc.Notice!.IsStanding);

        // 用关闭按钮关掉：仍是"待确认"，但提示条不再自动回来（别的提示关闭后、保存后都不回来）
        tab.Doc.DismissNotice();
        Assert.Null(tab.Doc.Notice);
        Assert.False(tab.Doc.Encoding.IsCertain);
        tab.Doc.Notice = new DocumentNotice("别的提示", "", NoticeSeverity.Informational, []);
        tab.Doc.DismissNotice();
        Assert.Null(tab.Doc.Notice);

        // 顶部"编码"菜单里仍可确认、更换编码重试
        var items = window.BuildEncodingMenuItems(tab).OfType<MenuItem>().ToList();
        Assert.Contains(items, i => ((string)i.Header!).StartsWith("确认 "));
        var reopen = Assert.Single(items, i => (string?)i.Header == "以其他编码重新打开");
        reopen.Items.OfType<MenuItem>().Single(i => ((string)i.Header!).StartsWith("Big5")).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // 又在试编码了：提示条重新出现，等待 ✓
        Assert.Equal("中文", tab.Doc.Document.Text);
        Assert.Equal("请确认当前文件的编码格式", tab.Doc.Notice?.Title);
        Assert.Equal("Big5（繁体中文）", Switcher(tab.Doc).Label);
    }

    [AvaloniaFact]
    public async Task NoticeBar_CanBeDragged_WithinTheEditorArea()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "drag.txt", Encoding.GetEncoding(936).GetBytes("你好世界"));
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();

        var host = window.FindControl<Border>("NoticeHost")!;
        var area = window.FindControl<Border>("EditorArea")!;
        Assert.True(host.IsVisible);
        Assert.Equal(default, window.NoticeOffset);

        // 按在标题文字附近（不是按钮）拖动
        var grab = host.TranslatePoint(new Avalonia.Point(80, 18), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Avalonia.Point(200, 150));
        window.MouseUp(grab + new Avalonia.Point(200, 150), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Avalonia.Vector(200, 150), window.NoticeOffset);

        // 拖得太远：停在编辑区边上，整个提示条仍在编辑区内
        grab = host.TranslatePoint(new Avalonia.Point(80, 18), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Avalonia.Point(5000, 5000));
        window.MouseUp(grab + new Avalonia.Point(5000, 5000), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var bounds = new Avalonia.Rect(host.TranslatePoint(default, window)!.Value, host.Bounds.Size);
        var areaBounds = new Avalonia.Rect(area.TranslatePoint(default, window)!.Value, area.Bounds.Size);
        Assert.True(areaBounds.Contains(bounds), $"{bounds} 不在 {areaBounds} 内");
        Assert.Equal(areaBounds.Right, bounds.Right, 0.5);
        Assert.Equal(areaBounds.Bottom, bounds.Bottom, 0.5);

        // 按在按钮上不会拖动，按钮照常工作
        var before = window.NoticeOffset;
        var check = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is FASymbolIcon);
        var onCheck = check.TranslatePoint(new Avalonia.Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
        window.MouseDown(onCheck, MouseButton.Left);
        window.MouseMove(onCheck + new Avalonia.Point(-100, -100));
        window.MouseUp(onCheck + new Avalonia.Point(-100, -100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, window.NoticeOffset);

        // 双击空白处回到左上角
        grab = host.TranslatePoint(new Avalonia.Point(80, 18), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseUp(grab, MouseButton.Left);
        window.MouseDown(grab, MouseButton.Left);
        window.MouseUp(grab, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(default, window.NoticeOffset);
    }

    [AvaloniaFact]
    public async Task ConfirmingEncoding_IsRemembered_ThenOffersUtf8Conversion()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "confirm.txt", Encoding.GetEncoding(936).GetBytes("中文"));

        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Action(tab.Doc, "显示正常").Execute();
        Assert.True(tab.Doc.Encoding.IsCertain);
        Assert.False(tab.Doc.IsDirty); // 确认编码不改动文件
        Assert.Equal(54936, App.EncodingMemory.Get(path)?.CodePage);

        // 点了 ✓ 之后再问是否转换为 UTF-8；选择保持则关闭提示条
        Assert.Equal("编码已确认", tab.Doc.Notice?.Title);
        Assert.Equal(["转换为 UTF-8", "保持 GB18030"], tab.Doc.Notice!.Actions.Select(a => a.Label));
        Action(tab.Doc, "保持 GB18030").Execute();
        Assert.Null(tab.Doc.Notice);

        Assert.True(await window.CloseTabAsync(tab));
        await window.OpenFilesAsync([path]);
        var reopened = window.ActiveTab!.Doc;
        Assert.True(reopened.Encoding.IsCertain);
        Assert.Null(reopened.Notice);
        Assert.Equal("GB18030", window.FindControl<Button>("EncodingButton")!.Content);
    }

    [AvaloniaFact]
    public async Task ChoosingAnotherEncoding_AsksAgain_DimsTriedOnes_AndRemembersAfterCheckmark()
    {
        var (window, library) = CreateWindow();
        // 太短，按结构推测会猜成 GB18030
        var path = WriteFile(library, "big5.txt", Encoding.GetEncoding(950).GetBytes("中文"));

        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;
        Assert.NotEqual("中文", doc.Document.Text);
        int guessed = doc.Encoding.CodePage;

        // 先试 Shift-JIS，再试 Big5
        Assert.Equal("更换编码重试", Switcher(doc).Label);
        Choice(doc, "Shift-JIS").Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Shift-JIS（日文）", Switcher(doc).Label); // 换过一次后显示当前编码名
        Choice(doc, "Big5").Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Big5（繁体中文）", Switcher(doc).Label);
        Assert.Equal("中文", doc.Document.Text);
        Assert.Equal(950, doc.Encoding.CodePage);

        // 换了编码后仍要点 ✓ 确认，确认前不记录
        Assert.False(doc.Encoding.IsCertain);
        Assert.Equal("请确认当前文件的编码格式", doc.Notice?.Title);
        Assert.StartsWith("已按 Big5（繁体中文） 重新打开", doc.Notice!.Message);
        Assert.Null(App.EncodingMemory.Get(path));

        // 检测时考虑过的、打开时猜的和试过的两个都是灰色，但仍然可以选择
        Assert.Contains(65001, doc.TriedCodePages);
        Assert.Equal([guessed, 932, 950], doc.TriedCodePages.Skip(doc.TriedCodePages.Count - 3));
        var dimmed = Switcher(doc).Children!.Where(c => c.IsDimmed).Select(c => c.Label.Split('（')[0]).ToList();
        Assert.Contains("Big5", dimmed);
        Assert.Contains("Shift-JIS", dimmed);

        Action(doc, "显示正常").Execute();
        Assert.True(doc.Encoding.IsCertain);
        Assert.Equal(950, App.EncodingMemory.Get(path)?.CodePage);
        Assert.Equal("编码已确认", doc.Notice?.Title);

        // 接着转换为 UTF-8：编码已确认，不再弹对话框
        Action(doc, "转换为 UTF-8").Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("中文")], File.ReadAllBytes(path));
        Assert.Null(App.EncodingMemory.Get(path));
    }

    [AvaloniaFact]
    public async Task TopMenu_AlwaysOffersEncodingChoicesAndConversion()
    {
        var (window, library) = CreateWindow();
        var path = WriteFile(library, "menu.txt", Encoding.GetEncoding(936).GetBytes(LongGbk));
        await window.OpenFilesAsync([path]);
        var tab = window.ActiveTab!;
        Assert.True(tab.Doc.Encoding.IsCertain); // 统计检测足够可靠：没有提示条，但菜单里照样可以换编码、转换
        Assert.Null(tab.Doc.Notice);

        var menu = window.FindControl<MenuItem>("EncodingMenu")!;
        Assert.Equal("编码(_N)", menu.Header);
        var items = window.BuildEncodingMenuItems(tab).OfType<MenuItem>().ToList();
        var reopen = Assert.Single(items, i => (string?)i.Header == "以其他编码重新打开");
        Assert.True(reopen.IsEnabled);
        Assert.Equal(ReopenCount, reopen.Items.Count);
        // 顶部菜单：当前编码打勾但不变灰；检测时试过的 UTF-8 变灰，仍可选择
        var gb = reopen.Items.OfType<MenuItem>().Single(i => ((string)i.Header!).StartsWith("GB18030"));
        Assert.True(gb.IsChecked);
        Assert.DoesNotContain("dimmed", gb.Classes);
        var utf8Item = reopen.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "UTF-8");
        Assert.Contains("dimmed", utf8Item.Classes);
        Assert.True(utf8Item.IsEnabled);
        Assert.Contains(items, i => (string?)i.Header == "以其他编码保存");
        Assert.True(Assert.Single(items, i => (string?)i.Header == "转换为 UTF-8").IsEnabled);

        // UTF-8 文件：转换项仍在，只是不可用
        var utf8 = WriteFile(library, "u.txt", Encoding.UTF8.GetBytes("中文"));
        await window.OpenFilesAsync([utf8]);
        var convert = Assert.Single(window.BuildEncodingMenuItems(window.ActiveTab!).OfType<MenuItem>(),
            i => ((string)i.Header!).StartsWith("转换为 UTF-8"));
        Assert.False(convert.IsEnabled);
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
        Assert.Equal(["更换编码重试"], tab.Doc.Notice!.Actions.Select(a => a.Label));
        Assert.False(tab.Doc.Notice.IsStanding);

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
