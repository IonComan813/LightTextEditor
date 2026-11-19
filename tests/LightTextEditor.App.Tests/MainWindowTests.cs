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
        App.Settings = new AppSettings { LibraryPath = library, RestoreSession = false };
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
        Assert.False(tab.CloseButton.Classes.Contains("dirty"));
    }

    [AvaloniaFact]
    public void Typing_MarksTabDirty_AndUsesFirstLineAsTitle()
    {
        var (window, _) = CreateWindow();
        Type(window, "购物清单");
        var tab = window.ActiveTab!;
        Assert.True(tab.Doc.IsDirty);
        Assert.Equal("购物清单", tab.Doc.Title);
        Assert.True(tab.CloseButton.Classes.Contains("dirty"));
        Assert.StartsWith("● 购物清单", window.Title);
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

    [AvaloniaFact]
    public async Task CtrlS_OnUntitled_SavesIntoLibrary_WithoutDialog()
    {
        var (window, library) = CreateWindow();
        Type(window, "# 会议记录\n内容");
        Assert.True(await window.SaveActiveAsync());

        var tab = window.ActiveTab!;
        var expected = Path.Combine(library, "会议记录.md");
        Assert.Equal(expected, tab.Doc.FilePath);
        Assert.False(tab.Doc.IsDirty);
        Assert.False(tab.CloseButton.Classes.Contains("dirty"));
        Assert.Equal("会议记录.md", tab.Doc.Title);

        var bytes = File.ReadAllBytes(expected);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // .md 默认带 BOM
        Assert.Contains("会议记录", Encoding.UTF8.GetString(bytes));

        // 再次保存写回同一个文件，不会生成新文件
        Type(window, "!");
        Assert.True(await window.SaveActiveAsync());
        Assert.Single(Directory.GetFiles(library));
    }

    [AvaloniaFact]
    public async Task OpeningFile_ReplacesPristineUntitledTab()
    {
        var (window, library) = CreateWindow();
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "a.txt");
        File.WriteAllText(path, "hello");

        await window.OpenFilesAsync([path]);
        var tab = Assert.Single(window.AllTabs());
        Assert.Equal(path, tab.Doc.FilePath);
        Assert.Equal("hello", tab.Doc.Document.Text);

        // 再打开同一个文件只会切换过去，不会重复打开
        await window.OpenFilesAsync([path]);
        Assert.Single(window.AllTabs());
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
        Assert.Equal("是否将更改保存到“未保存”？", dialog.Content);
        dialog.Hide(answer);
        await closing;

        Assert.Equal(remainingTabs, window.AllTabs().Count);
        Assert.False(Directory.Exists(library) && Directory.GetFiles(library).Length > 0);
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
        Assert.True(File.Exists(Path.Combine(library, "要保存的.md")));
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
        window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, "1"); // 选中 s1
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
}
