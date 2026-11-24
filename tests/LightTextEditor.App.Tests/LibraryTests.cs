using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class LibraryTests
{
    private static (MainWindow Window, string Library, string Outside) CreateWindow(bool sampleCreated = true)
    {
        var root = Path.Combine(TestAppBuilder.Root, "libt-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "library");
        var outside = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outside);
        App.Settings = new AppSettings { SampleNoteCreated = sampleCreated, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 900, Height = 600 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library, outside);
    }

    [AvaloniaFact]
    public async Task FirstRun_PutsSampleNoteIntoLibrary_AndOpensIt()
    {
        var (window, library, _) = CreateWindow(sampleCreated: false);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        var note = Path.Combine(library, SampleNote.FileName);
        Assert.True(File.Exists(note));
        Assert.True(File.Exists(Path.Combine(library, "assets", "示例图片.png")));
        Assert.Contains("# Markdown 示例", File.ReadAllText(note));
        Assert.Equal(note, window.ActiveTab!.Doc.FilePath);
        Assert.True(App.Settings.SampleNoteCreated);
        Assert.True(File.Exists(Path.Combine(library, SampleNote.HtmlFileName))); // HTML 示例也放一份

        // 删掉之后不会在下次启动时再生成
        File.Delete(note);
        var second = new MainWindow();
        second.Initialize([]);
        Assert.False(File.Exists(note));
    }

    [AvaloniaFact]
    public async Task OpeningExternalFile_CreatesShortcutInLibrary_ButSavesInPlace()
    {
        var (window, library, outside) = CreateWindow();
        var external = Path.Combine(outside, "外部.txt");
        File.WriteAllText(external, "原内容");
        await window.OpenFilesAsync([external]);

        var shortcuts = Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library));
        var shortcut = Assert.Single(shortcuts);
        Assert.True(LibraryShortcuts.TryResolve(shortcut, out var target));
        Assert.Equal(external, target);

        // 再次打开不会重复创建
        await window.OpenFilesAsync([external]);
        Assert.Single(Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library)));

        // 保存仍写回原位置
        window.ActiveTab!.Doc.Document.Insert(0, "新");
        Assert.True(await window.SaveActiveAsync());
        Assert.Equal("新原内容", File.ReadAllText(external));
        Assert.Equal(external, window.ActiveTab.Doc.FilePath);
    }

    [AvaloniaFact]
    public async Task OpeningTheShortcut_OpensTheRealFile()
    {
        var (window, library, outside) = CreateWindow();
        var external = Path.Combine(outside, "real.md");
        File.WriteAllText(external, "x");
        var shortcut = LibraryShortcuts.Ensure(library, external)!;

        await window.OpenFilesAsync([shortcut]);
        Assert.Equal(external, window.ActiveTab!.Doc.FilePath);

        // 保存后快捷方式仍然是快捷方式（没有被替换成普通文件）
        window.ActiveTab.Doc.Document.Insert(0, "y");
        await window.SaveActiveAsync();
        Assert.True(LibraryShortcuts.TryResolve(shortcut, out var t));
        Assert.Equal(external, t);
        Assert.Equal("yx", File.ReadAllText(external));
    }

    [AvaloniaFact]
    public async Task LibraryFiles_GetNoShortcut()
    {
        var (window, library, _) = CreateWindow();
        Directory.CreateDirectory(library);
        var inside = Path.Combine(library, "内部.txt");
        File.WriteAllText(inside, "a");
        await window.OpenFilesAsync([inside]);
        Assert.False(Directory.Exists(LibraryShortcuts.FolderPath(library)));
        Assert.False(window.IsExternalFile(window.ActiveTab!.Doc));
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_MovesFile_AndRemovesShortcut()
    {
        var (window, library, outside) = CreateWindow();
        var external = Path.Combine(outside, "搬家.txt");
        File.WriteAllText(external, "内容");
        await window.OpenFilesAsync([external]);
        var tab = window.ActiveTab!;
        Assert.True(window.IsExternalFile(tab.Doc));

        var editorTop = tab.Editor.TranslatePoint(default, window)!.Value.Y;
        Assert.True(await window.MoveToLibraryAsync(tab, confirm: false));
        Dispatcher.UIThread.RunJobs();
        // 提示浮在正文上方，不把编辑器往下挤
        Assert.NotNull(tab.Doc.Notice);
        Assert.True(window.FindControl<Avalonia.Controls.Border>("NoticeHost")!.IsVisible);
        Assert.Equal(editorTop, tab.Editor.TranslatePoint(default, window)!.Value.Y);
        var moved = Path.Combine(library, "搬家.txt");
        Assert.Equal(moved, tab.Doc.FilePath);
        Assert.False(File.Exists(external));
        Assert.Equal("内容", File.ReadAllText(moved));
        Assert.Empty(Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library)));
        Assert.False(window.IsExternalFile(tab.Doc));
    }

    private static FluentAvalonia.UI.Controls.FAContentDialog FindDialog(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<FluentAvalonia.UI.Controls.FAContentDialog>().Single();
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_AsksForAnotherName_WhenNameAndExtensionAreTaken()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "同名.txt"), "库里的");
        var external = Path.Combine(outside, "同名.txt");
        File.WriteAllText(external, "外面的");
        await window.OpenFilesAsync([external]);

        var moving = window.MoveToLibraryAsync(window.ActiveTab!, confirm: false);
        var dialog = FindDialog(window);
        var panel = (StackPanel)dialog.Content!;
        Assert.Contains(panel.Children.OfType<TextBlock>(), t => t.Text == "笔记库中已有“同名.txt”。请换一个文件名：");
        Assert.False(dialog.IsPrimaryButtonEnabled); // 名字没改不能移动
        Assert.Contains(panel.Children.OfType<TextBlock>(), t => t.IsVisible && t.Text?.StartsWith("笔记库中已有“同名.txt”，请换一个文件名") == true);

        var nameBox = (TextBox)panel.Children.OfType<Grid>().Single().Children[0];
        nameBox.Text = "外面的同名";
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.IsPrimaryButtonEnabled);
        dialog.Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.Primary);
        Assert.True(await moving);

        Assert.Equal(Path.Combine(library, "外面的同名.txt"), window.ActiveTab!.Doc.FilePath);
        Assert.Equal("库里的", File.ReadAllText(Path.Combine(library, "同名.txt")));
        Assert.Equal("外面的", File.ReadAllText(Path.Combine(library, "外面的同名.txt")));
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_CanBeCancelled_WhenNameIsTaken()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "同名.txt"), "库里的");
        var external = Path.Combine(outside, "同名.txt");
        File.WriteAllText(external, "外面的");
        await window.OpenFilesAsync([external]);

        var moving = window.MoveToLibraryAsync(window.ActiveTab!, confirm: false);
        FindDialog(window).Hide(FluentAvalonia.UI.Controls.FAContentDialogResult.None);
        Assert.False(await moving);
        Assert.Equal(external, window.ActiveTab!.Doc.FilePath);
        Assert.True(File.Exists(external));
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_SameNameWithAnotherExtension_IsNotAConflict()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "笔记.md"), "库里的");
        var external = Path.Combine(outside, "笔记.txt");
        File.WriteAllText(external, "外面的");
        await window.OpenFilesAsync([external]);
        Assert.True(await window.MoveToLibraryAsync(window.ActiveTab!, confirm: false)); // 不弹对话框
        Assert.Equal(Path.Combine(library, "笔记.txt"), window.ActiveTab!.Doc.FilePath);
    }

    [AvaloniaFact]
    public void Shortcuts_WithTheSameName_GetTheirParentFolderNames()
    {
        var (_, library, outside) = CreateWindow();
        string Make(params string[] parts)
        {
            var path = Path.Combine([outside, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, path);
            return path;
        }
        string[] Names() => Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library))
            .Select(e => Path.GetFileName(e).Replace(".lnk", "")).Order(StringComparer.Ordinal).ToArray();

        var first = Make("a", "hdrplayer", "readme.md");
        LibraryShortcuts.Ensure(library, first);
        Assert.Equal(["readme.md"], Names()); // 只有一个时就是文件名

        // 第二个同名文件：两个都加上父文件夹名
        var second = Make("b", "docs", "readme.md");
        LibraryShortcuts.Ensure(library, second);
        Assert.Equal(["docs／readme.md", "hdrplayer／readme.md"], Names());

        // 父文件夹也同名：再加一层
        var third = Make("c", "hdrplayer", "readme.md");
        LibraryShortcuts.Ensure(library, third);
        Assert.Equal(["a／hdrplayer／readme.md", "b／docs／readme.md", "c／hdrplayer／readme.md"], Names());

        // 两层父文件夹名也相同：加编号
        var fourth = Make("x", "c", "hdrplayer", "readme.md");
        LibraryShortcuts.Ensure(library, fourth);
        Assert.Equal(["a／hdrplayer／readme.md", "b／docs／readme.md", "c／hdrplayer／readme (2).md", "c／hdrplayer／readme.md"], Names());
        Assert.Equal(fourth, ResolveShortcut(library, "c／hdrplayer／readme (2).md"));
        var fifth = Make("y", "c", "hdrplayer", "readme.md");
        LibraryShortcuts.Ensure(library, fifth); // 已有的不再改名
        Assert.Equal(["a／hdrplayer／readme.md", "b／docs／readme.md", "c／hdrplayer／readme (2).md", "c／hdrplayer／readme (3).md", "c／hdrplayer／readme.md"], Names());
        Assert.Equal(fifth, ResolveShortcut(library, "c／hdrplayer／readme (3).md"));

        // 都仍指向原来的文件；别的文件名不受影响
        Assert.Equal(first, ResolveShortcut(library, "a／hdrplayer／readme.md"));
        Assert.Equal(third, ResolveShortcut(library, "c／hdrplayer／readme.md"));
        LibraryShortcuts.Ensure(library, Make("d", "notes.txt"));
        Assert.Contains("notes.txt", Names());
        Assert.Equal(first, File.ReadAllText(first));
    }

    [AvaloniaFact]
    public void Shortcuts_RenamedByTheUser_AreLeftAlone()
    {
        var (_, library, outside) = CreateWindow();
        var first = Path.Combine(outside, "x", "readme.md");
        var second = Path.Combine(outside, "y", "readme.md");
        foreach (var p in new[] { first, second }) { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); }
        var shortcut = LibraryShortcuts.Ensure(library, first)!;
        var renamed = Path.Combine(Path.GetDirectoryName(shortcut)!, "我的说明" + (OperatingSystem.IsWindows() ? ".lnk" : ""));
        File.Move(shortcut, renamed);
        LibraryShortcuts.Ensure(library, second);
        Assert.True(File.Exists(renamed) || new FileInfo(renamed).LinkTarget is not null);
        Assert.Equal(first, ResolveShortcut(library, "我的说明"));
        Assert.Equal(second, ResolveShortcut(library, "y／readme.md"));
    }

    private static string ResolveShortcut(string library, string name)
    {
        var path = Path.Combine(LibraryShortcuts.FolderPath(library), name + (OperatingSystem.IsWindows() ? ".lnk" : ""));
        Assert.True(LibraryShortcuts.TryResolve(path, out var target), name);
        return target;
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_BringsImagesAlong()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(Path.Combine(outside, "assets"));
        Directory.CreateDirectory(Path.Combine(outside, "pics"));
        File.WriteAllBytes(Path.Combine(outside, "assets", "a.png"), [1, 2, 3]);       // 文档自己的图片：移动，链接不变
        File.WriteAllBytes(Path.Combine(outside, "assets", "shared.png"), [4]);       // 另一篇笔记也在用：复制
        File.WriteAllBytes(Path.Combine(outside, "assets", "taken.png"), [5]);        // 笔记库已有同名的不同图片：改名
        File.WriteAllBytes(Path.Combine(outside, "pics", "my pic.png"), [6]);         // 子文件夹、名字带空格
        var parent = Path.GetDirectoryName(outside)!;
        File.WriteAllBytes(Path.Combine(parent, "up.png"), [7]);                      // 文档文件夹以外：复制到 assets
        File.WriteAllText(Path.Combine(outside, "other.md"), "![](assets/shared.png)");
        Directory.CreateDirectory(Path.Combine(library, "assets"));
        File.WriteAllBytes(Path.Combine(library, "assets", "taken.png"), [9, 9]);

        var text = string.Join("\n",
            "# 图片",
            "![a](assets/a.png)",
            "![s](assets/shared.png)",
            "![t](assets/taken.png)",
            "![p](<pics/my pic.png>)",
            "![u](../up.png)",
            "<img src=\"assets/taken.png\" width=\"100\">",
            "![web](https://example.com/x.png)",
            "![missing](assets/none.png)");
        var external = Path.Combine(outside, "图文.md");
        File.WriteAllText(external, text);
        await window.OpenFilesAsync([external]);
        var tab = window.ActiveTab!;
        Assert.True(await window.MoveToLibraryAsync(tab, confirm: false));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(library, "assets", "a.png")));
        Assert.False(File.Exists(Path.Combine(outside, "assets", "a.png")));
        Assert.True(File.Exists(Path.Combine(library, "assets", "shared.png")));
        Assert.True(File.Exists(Path.Combine(outside, "assets", "shared.png")));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(Path.Combine(library, "assets", "taken.png")));
        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(Path.Combine(library, "assets", "taken-2.png")));
        Assert.True(File.Exists(Path.Combine(library, "pics", "my pic.png")));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(library, "assets", "up.png")));
        Assert.True(File.Exists(Path.Combine(parent, "up.png")));

        var expected = string.Join("\n",
            "# 图片",
            "![a](assets/a.png)",
            "![s](assets/shared.png)",
            "![t](assets/taken-2.png)",
            "![p](<pics/my pic.png>)",
            "![u](assets/up.png)",
            "<img src=\"assets/taken-2.png\" width=\"100\">",
            "![web](https://example.com/x.png)",
            "![missing](assets/none.png)");
        Assert.Equal(expected, tab.Doc.Document.Text);
        Assert.False(tab.Doc.IsDirty); // 改写链接后已保存
        Assert.Equal(expected, File.ReadAllText(tab.Doc.FilePath!));
    }

    [AvaloniaFact]
    public async Task SaveAs_FromLibraryToOutside_LeavesShortcutInLibrary()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(library);
        var note = Path.Combine(library, "库内.txt");
        File.WriteAllText(note, "内容");
        await window.OpenFilesAsync([note]);
        var target = Path.Combine(outside, "另存.txt");
        Assert.True(await window.SaveAsToAsync(window.ActiveTab!, target));
        Assert.Equal(target, window.ActiveTab!.Doc.FilePath);
        var shortcut = Assert.Single(Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library)));
        Assert.True(LibraryShortcuts.TryResolve(shortcut, out var resolved));
        Assert.Equal(target, resolved);
        Assert.True(File.Exists(note)); // 原来的笔记仍在笔记库里
    }

    [AvaloniaFact]
    public async Task RenamingExternalFile_UpdatesShortcut()
    {
        var (window, library, outside) = CreateWindow();
        var external = Path.Combine(outside, "a.txt");
        File.WriteAllText(external, "a");
        await window.OpenFilesAsync([external]);
        await window.ChangeExtensionAsync(window.ActiveTab!, ".md");
        var shortcut = Assert.Single(Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library)));
        Assert.True(LibraryShortcuts.TryResolve(shortcut, out var t));
        Assert.Equal(Path.Combine(outside, "a.md"), t);
    }

    [AvaloniaFact]
    public void SelectionBrush_IsTranslucent()
    {
        var (window, _, _) = CreateWindow();
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(window.ActiveTab!.Editor.TextArea.SelectionBrush);
        Assert.True(brush.Color.A < 0x80);
        Assert.Null(window.ActiveTab.Editor.TextArea.SelectionForeground);
    }
}
