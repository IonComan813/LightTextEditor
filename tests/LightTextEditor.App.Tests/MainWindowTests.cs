using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

public class MainWindowTests
{
    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "lib-" + Guid.NewGuid().ToString("N"));
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow();
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    private static void Type(MainWindow window, string text)
    {
        window.ActiveTab!.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button CloseButtonOf(DocumentTab tab) => tab.CloseButton;

    [AvaloniaFact]
    public void StartsWithOneUntitledTab()
    {
        var (window, _) = CreateWindow();
        var tab = Assert.Single(window.AllTabs());
        Assert.True(tab.Doc.IsUntitled);
        Assert.False(tab.Doc.IsDirty);
        Assert.Equal("无标题", tab.Doc.Title);
        Assert.DoesNotContain("dirty", tab.CloseButton.Classes);
    }

    [AvaloniaFact]
    public void Typing_MarksTabDirty_TitleStaysUntitled()
    {
        var (window, _) = CreateWindow();
        Type(window, "购物清单");
        var tab = window.ActiveTab!;
        Assert.True(tab.Doc.IsDirty);
        Assert.Equal("无标题", tab.Doc.Title); // 保存前不跟着第一行变化
        Assert.Contains("dirty", tab.CloseButton.Classes);
        Assert.StartsWith("● 无标题", window.Title);
    }

    [AvaloniaFact]
    public void UndoToSavedState_ClearsDirty()
    {
        var (window, _) = CreateWindow();
        Type(window, "a");
        Assert.True(window.ActiveTab!.Doc.IsDirty);
        window.ActiveTab.Editor.Undo();
        Assert.False(window.ActiveTab.Doc.IsDirty);
    }

    /// <summary>文件名对话框里的“文件名”输入框和“后缀”下拉框。</summary>
    private static (TextBox Name, ComboBox Extension) FileNameFields(FluentAvalonia.UI.Controls.FAContentDialog dialog)
    {
        var panel = (StackPanel)dialog.Content!;
        var row = panel.Children.OfType<Grid>().Single();
        return ((TextBox)row.Children[0], (ComboBox)row.Children[1]);
    }

    [AvaloniaFact]
    public async Task CtrlS_OnUntitled_AsksForFileName_DefaultsToFirstLineAndTxt()
    {
        var (window, library) = CreateWindow();
        Type(window, "# 会议记录\n内容");
        var saving = window.SaveActiveAsync();

        var dialog = FindDialog(window);
        Assert.Equal("保存到笔记库", dialog.Title);
        var (name, ext) = FileNameFields(dialog);
        Assert.Equal("会议记录", name.Text);
        Assert.Equal(".txt", ext.Text);
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.Primary);
        Assert.True(await saving);

        var tab = window.ActiveTab!;
        var expected = Path.Combine(library, "会议记录.txt");
        Assert.Equal(expected, tab.Doc.FilePath);
        Assert.False(tab.Doc.IsDirty);
        Assert.DoesNotContain("dirty", tab.CloseButton.Classes);
        Assert.Equal("会议记录.txt", tab.Doc.Title);

        var bytes = File.ReadAllBytes(expected);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // .txt 默认带 BOM
        Assert.Contains("会议记录", Encoding.UTF8.GetString(bytes));

        // 再次保存直接写回同一个文件，不再弹窗
        Type(window, "!");
        Assert.True(await window.SaveActiveAsync());
        Assert.Single(Directory.GetFiles(library));
    }

    [AvaloniaFact]
    public async Task SaveDialog_CanChangeNameAndExtension()
    {
        var (window, library) = CreateWindow();
        Type(window, "print('hi')");
        var saving = window.SaveActiveAsync();
        var dialog = FindDialog(window);
        var (name, ext) = FileNameFields(dialog);
        name.Text = "hello";
        ext.Text = "py"; // 不带点也可以
        Dispatcher.UIThread.RunJobs();
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.Primary);
        Assert.True(await saving);

        var path = Path.Combine(library, "hello.py");
        Assert.Equal(path, window.ActiveTab!.Doc.FilePath);
        Assert.Equal((byte)'p', File.ReadAllBytes(path)[0]); // 代码文件不带 BOM
    }

    [AvaloniaFact]
    public async Task SaveDialog_RejectsExistingName()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "重复.txt"), "old");
        Type(window, "重复");
        var saving = window.SaveActiveAsync();
        var dialog = FindDialog(window);
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsPrimaryButtonEnabled);

        FileNameFields(dialog).Name.Text = "不重复";
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.IsPrimaryButtonEnabled);

        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None); // 取消
        Assert.False(await saving);
        Assert.True(window.ActiveTab!.Doc.IsUntitled);
        Assert.Equal("old", File.ReadAllText(Path.Combine(library, "重复.txt")));
    }

    [AvaloniaFact]
    public async Task ChangeExtension_RenamesFile_AndAdjustsBom()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "script.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'=', (byte)'1']);
        await window.OpenFilesAsync([path]);

        await window.ChangeExtensionAsync(window.ActiveTab!, ".py");
        var newPath = Path.Combine(library, "script.py");
        Assert.False(File.Exists(path));
        Assert.Equal(newPath, window.ActiveTab!.Doc.FilePath);
        Assert.Equal("x=1"u8.ToArray(), File.ReadAllBytes(newPath)); // BOM 已去掉
        Assert.False(window.ActiveTab.Doc.IsDirty);
        Assert.Equal("Python", window.FindControl<Button>("FileTypeButton")!.Content);
    }

    [AvaloniaFact]
    public async Task ChangeExtension_OnUntitled_ChangesDefaultInSaveDialog()
    {
        var (window, _) = CreateWindow();
        await window.ChangeExtensionAsync(window.ActiveTab!, "md");
        Assert.Equal("Markdown", window.FindControl<Button>("FileTypeButton")!.Content);
        Type(window, "x");
        var saving = window.SaveActiveAsync();
        var dialog = FindDialog(window);
        Assert.Equal(".md", FileNameFields(dialog).Extension.Text);
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        await saving;
    }

    [AvaloniaFact]
    public async Task OpeningFile_FromBlankTab_OpensInNewTab()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "hello");
        var blank = Assert.Single(window.AllTabs());

        await window.OpenFilesAsync([path]);
        Assert.Equal(2, window.AllTabs().Count);
        Assert.Same(blank, window.AllTabs()[0]); // 空白标签页保留
        var tab = window.ActiveTab!;
        Assert.Equal(path, tab.Doc.FilePath);
        Assert.Equal("hello", tab.Doc.Document.Text);

        // 再打开同一个文件只会切换过去，不会重复打开
        window.Select(blank);
        await window.OpenFilesAsync([path]);
        Assert.Equal(2, window.AllTabs().Count);
        Assert.Same(tab, window.ActiveTab);
    }

    [AvaloniaFact]
    public async Task GbkFile_IsOpenedAsUncertain_WithNotice()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        TextEncoding.EnsureCodePagesRegistered();
        var path = Path.Combine(library, "gbk.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(936).GetBytes("简体中文内容"));

        await window.OpenFilesAsync([path]);
        var doc = window.ActiveTab!.Doc;
        Assert.Equal("简体中文内容", doc.Document.Text);
        Assert.False(doc.Encoding.IsCertain);
        Assert.NotNull(doc.Notice);
        Assert.Equal("GB18030（未确认）", window.FindControl<Button>("EncodingButton")!.Content);
    }

    [AvaloniaFact]
    public async Task OpenDialog_OnlyBlocksItsOwnWindow()
    {
        var (a, _) = CreateWindow();
        var (b, _) = CreateWindow();
        var pending = Dialogs.ConfirmAsync(a, "标题", "内容", "确定");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Dialogs.IsOpenFor(a));
        Assert.False(Dialogs.IsOpenFor(b));

        b.NewDocument();
        Assert.True(await b.CloseTabAsync(b.ActiveTab!)); // 另一个窗口不受影响
        Assert.False(await a.CloseTabAsync(a.ActiveTab!)); // 有对话框的窗口不能关闭标签

        FindDialog(a).Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        await pending;
        Assert.False(Dialogs.IsOpenFor(a));
    }

    [AvaloniaFact]
    public async Task CloseTab_NotDirty_ClosesWithoutPrompt_AndKeepsOneTab()
    {
        var (window, _) = CreateWindow();
        window.NewDocument();
        Assert.Equal(2, window.AllTabs().Count);
        Assert.True(await window.CloseTabAsync(window.ActiveTab!));
        Assert.Single(window.AllTabs());
        Assert.True(await window.CloseTabAsync(window.ActiveTab!));
        Assert.Single(window.AllTabs()); // 关掉最后一个会自动补一个空白标签页
    }

    [AvaloniaFact]
    public async Task ExternalChange_ReloadsCleanDocument()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "ext.txt");
        File.WriteAllText(path, "v1");
        await window.OpenFilesAsync([path]);

        File.WriteAllText(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        window.CheckExternalChanges();
        Assert.Equal("v2", window.ActiveTab!.Doc.Document.Text);
        Assert.False(window.ActiveTab.Doc.IsDirty);
    }

    [AvaloniaFact]
    public async Task ExternalChange_OnDirtyDocument_AsksInsteadOfReloading()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "ext2.txt");
        File.WriteAllText(path, "v1");
        await window.OpenFilesAsync([path]);
        Type(window, "mine ");

        File.WriteAllText(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        window.CheckExternalChanges();
        var doc = window.ActiveTab!.Doc;
        Assert.Contains("mine", doc.Document.Text);
        Assert.Equal("文件已在外部被修改", doc.Notice?.Title);
    }

    [AvaloniaFact]
    public async Task DeletedFile_BecomesDirty()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "gone.txt");
        File.WriteAllText(path, "x");
        await window.OpenFilesAsync([path]);
        File.Delete(path);
        window.CheckExternalChanges();
        Assert.True(window.ActiveTab!.Doc.IsDirty);
        Assert.True(await window.SaveActiveAsync());
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public void DirtyDot_TurnsIntoCross_OnHover()
    {
        var (window, _) = CreateWindow();
        Type(window, "x");
        var button = window.ActiveTab!.CloseButton;
        var cross = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        var dot = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();

        Assert.True(dot.IsEffectivelyVisible);
        Assert.False(cross.IsEffectivelyVisible);

        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        Assert.False(dot.IsEffectivelyVisible);
        Assert.True(cross.IsEffectivelyVisible);

        window.MouseMove(new Point(500, 300));
        Dispatcher.UIThread.RunJobs();
        Assert.True(dot.IsEffectivelyVisible);
    }

    private static FluentAvalonia.UI.Controls.FAContentDialog FindDialog(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.FAContentDialog>().Single();
    }

    [AvaloniaTheory]
    [InlineData(FluentAvalonia.UI.Controls.FAContentDialogResult.None, 2)]      // 取消：标签页保留
    [InlineData(FluentAvalonia.UI.Controls.FAContentDialogResult.Secondary, 1)] // 不保存：直接关闭
    public async Task ClosingDirtyTab_AsksToSave(FluentAvalonia.UI.Controls.FAContentDialogResult answer, int remainingTabs)
    {
        var (window, library) = CreateWindow();
        window.NewDocument();
        Type(window, "未保存");
        var closing = window.CloseTabAsync(window.ActiveTab!);

        var dialog = FindDialog(window);
        Assert.Equal("保存更改", dialog.Title);
        Assert.Equal("不保存", dialog.SecondaryButtonText);
        Assert.Equal("未保存", FileNameFields(dialog).Name.Text);
        dialog.Hide(answer);
        await closing;

        Assert.Equal(remainingTabs, window.AllTabs().Count);
        Assert.False(Directory.Exists(library) && Directory.GetFiles(library).Length > 0);
    }

    [AvaloniaFact]
    public async Task ClosingDirtySavedFile_UsesSimpleSavePrompt()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "已有.txt");
        File.WriteAllText(path, "a");
        await window.OpenFilesAsync([path]);
        Type(window, "b");
        var closing = window.CloseTabAsync(window.ActiveTab!);
        var dialog = FindDialog(window);
        Assert.Equal("是否将更改保存到“已有.txt”？", dialog.Content);
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.Primary);
        Assert.True(await closing);
        Assert.Equal("ba", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task ClosingDirtyTab_Save_WritesFileThenCloses()
    {
        var (window, library) = CreateWindow();
        window.NewDocument();
        Type(window, "要保存的");
        var closing = window.CloseTabAsync(window.ActiveTab!);
        FindDialog(window).Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.Primary);
        Assert.True(await closing);
        Assert.Single(window.AllTabs());
        Assert.True(File.Exists(Path.Combine(library, "要保存的.txt")));
    }

    [AvaloniaFact]
    public async Task Session_IsSavedOnClose_AndRestored()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var a = Path.Combine(library, "s1.txt");
        var b = Path.Combine(library, "s2.txt");
        File.WriteAllText(a, "aaaa");
        File.WriteAllText(b, "bbbbbbbb");
        await window.OpenFilesAsync([a, b]);
        window.NewDocument(); // 未保存的空白标签页不进入会话
        window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, "2"); // 选中 s1（第 1 个是启动时的空白标签页）
        Dispatcher.UIThread.RunJobs();
        window.ActiveTab!.Editor.CaretOffset = 3;
        window.Close();

        App.Settings.RestoreSession = true;
        var restored = new MainWindow();
        restored.Initialize([]);
        restored.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([a, b], restored.AllTabs().Select(t => t.Doc.FilePath));
        Assert.Equal(a, restored.ActiveTab!.Doc.FilePath);
        Assert.Equal(3, restored.ActiveTab.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public void CtrlN_And_CtrlTab_Work()
    {
        var (window, _) = CreateWindow();
        window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.AllTabs().Count);
        var second = window.ActiveTab;
        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(second, window.ActiveTab);
    }

    [AvaloniaFact]
    public async Task StartingWithMissingFile_ShowsError_ThenLeavesBlankTab()
    {
        var library = Path.Combine(TestAppBuilder.Root, "lib-" + Guid.NewGuid().ToString("N"));
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow();
        window.Initialize([Path.Combine(library, "不存在.md")]); // 窗口显示之前
        window.Show();
        await Task.Delay(50);
        var dialog = FindDialog(window); // 窗口显示后弹出“找不到文件”
        Assert.Equal("无法打开", dialog.Title);
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        for (int i = 0; i < 10 && window.AllTabs().Count == 0; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.Single(window.AllTabs());
        Assert.True(window.ActiveTab!.Doc.IsUntitled);
    }
}
