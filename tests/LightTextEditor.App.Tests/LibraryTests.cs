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

        Assert.True(await window.MoveToLibraryAsync(tab, confirm: false));
        var moved = Path.Combine(library, "搬家.txt");
        Assert.Equal(moved, tab.Doc.FilePath);
        Assert.False(File.Exists(external));
        Assert.Equal("内容", File.ReadAllText(moved));
        Assert.Empty(Directory.GetFileSystemEntries(LibraryShortcuts.FolderPath(library)));
        Assert.False(window.IsExternalFile(tab.Doc));
    }

    [AvaloniaFact]
    public async Task MoveToLibrary_KeepsBothFiles_WhenNameIsTaken()
    {
        var (window, library, outside) = CreateWindow();
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "同名.txt"), "库里的");
        var external = Path.Combine(outside, "同名.txt");
        File.WriteAllText(external, "外面的");
        await window.OpenFilesAsync([external]);
        Assert.True(await window.MoveToLibraryAsync(window.ActiveTab!, confirm: false));
        Assert.Equal(Path.Combine(library, "同名 (2).txt"), window.ActiveTab!.Doc.FilePath);
        Assert.Equal("库里的", File.ReadAllText(Path.Combine(library, "同名.txt")));
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
