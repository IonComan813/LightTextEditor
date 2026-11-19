using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Library;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Views;

// 文件操作：新建、打开、保存、重命名、关闭、外部修改检测
public partial class MainWindow
{
    private const long LargeFileThreshold = 50L * 1024 * 1024;

    private static string LibraryPath => Settings.ResolveLibraryPath();

    internal DocumentTab NewDocument()
    {
        var ext = Settings.DefaultExtension;
        var doc = DocumentViewModel.CreateNew(ext, TextEncoding.Utf8(Settings.ShouldWriteBom(ext)), Settings.NewFileLineEnding);
        return AddTab(doc);
    }

    private DocumentTab? FindTab(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return AllTabs().FirstOrDefault(t => t.Doc.FilePath is { } p && string.Equals(p, full, comparison));
    }

    // ───────────────────────── 打开 ─────────────────────────

    private async Task OpenWithPickerAsync()
    {
        Directory.CreateDirectory(LibraryPath);
        var start = await StorageProvider.TryGetFolderFromPathAsync(
            _active?.Doc.FilePath is { } p ? Path.GetDirectoryName(p)! : LibraryPath);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开",
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType("所有文件") { Patterns = ["*"] },
                new FilePickerFileType("文本文档") { Patterns = ["*.txt", "*.md", "*.markdown", "*.log"] },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await OpenFilesAsync(paths);
    }

    internal async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        DocumentTab? last = null;
        foreach (var path in paths)
        {
            var tab = await OpenFileAsync(path);
            if (tab is not null) last = tab;
        }
        if (last is not null) Select(last);
    }

    private async Task<DocumentTab?> OpenFileAsync(string path)
    {
        if (FindTab(path) is { } existing) return existing;

        try
        {
            if (Directory.Exists(path))
            {
                await Dialogs.ShowErrorAsync(this, "无法打开", $"“{path}”是一个文件夹。");
                return null;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                await Dialogs.ShowErrorAsync(this, "无法打开", $"找不到文件：\n{path}");
                return null;
            }
            if (info.Length > LargeFileThreshold &&
                !await Dialogs.ConfirmAsync(this, "文件较大",
                    $"“{info.Name}”有 {info.Length / 1024 / 1024} MB，打开可能较慢。仍要打开吗？", "打开"))
                return null;

            var loaded = TextFileIO.Load(path, Settings.NewFileLineEnding);
            if (loaded.LooksBinary &&
                !await Dialogs.ConfirmAsync(this, "可能不是文本文件",
                    $"“{info.Name}”看起来是二进制文件，以文本方式打开并保存可能会损坏它。仍要打开吗？", "打开"))
                return null;

            return AddLoadedDocument(path, loaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "无法打开", $"{path}\n\n{e.Message}");
            return null;
        }
    }

    /// <summary>恢复会话时使用：不弹任何对话框，失败就跳过。</summary>
    private DocumentTab? TryOpenFileSilently(string path)
    {
        try
        {
            if (FindTab(path) is { } existing) return existing;
            if (new FileInfo(path).Length > LargeFileThreshold) return null;
            var loaded = TextFileIO.Load(path, Settings.NewFileLineEnding);
            return loaded.LooksBinary ? null : AddLoadedDocument(path, loaded, select: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private DocumentTab AddLoadedDocument(string path, LoadedText loaded, bool select = true)
    {
        var doc = DocumentViewModel.FromFile(path, loaded);
        if (!loaded.Encoding.IsCertain) doc.Notice = UncertainEncodingNotice(doc);
        else if (loaded.HasMixedLineEndings)
            doc.Notice = new DocumentNotice("换行符不一致",
                $"此文件混用了多种换行符，主要为 {loaded.LineEnding.DisplayName()}。可在状态栏统一。",
                NoticeSeverity.Informational, []);

        // 当前标签页是空白的未命名文档时，直接替换掉它
        var pristine = _active is { Doc.IsPristine: true } ? _active : null;
        var tab = AddTab(doc, select);
        if (pristine is not null && select) RemoveTab(pristine);
        return tab;
    }

    private DocumentNotice UncertainEncodingNotice(DocumentViewModel doc) => new(
        "无法确定编码",
        $"已按 {doc.Encoding.DisplayName} 打开。如果显示乱码，请在状态栏右下角选择其他编码重新打开。",
        NoticeSeverity.Warning,
        [
            new NoticeAction("显示正常，确认此编码", () =>
            {
                doc.Encoding = doc.Encoding with { IsCertain = true };
                doc.Notice = null;
            }),
            new NoticeAction("以 UTF-8 重新打开", () => _ = ReopenWithEncodingAsync(doc, TextEncoding.Utf8(false))),
        ]);

    // ───────────────────────── 保存 ─────────────────────────

    internal Task<bool> SaveActiveAsync(bool saveAs = false) =>
        _active is null ? Task.FromResult(false) : SaveAsync(_active, saveAs);

    /// <summary>
    /// 保存文档。未命名文档会弹出对话框确认文件名和后缀，保存到笔记库（不询问位置）。
    /// </summary>
    private async Task<bool> SaveAsync(DocumentTab tab, bool saveAs = false)
    {
        var doc = tab.Doc;

        if (saveAs)
        {
            var picked = await PickSavePathAsync(doc);
            if (picked is null) return false;
            var encoding = doc.IsUntitled
                ? TextEncoding.Utf8(Settings.ShouldWriteBom(Path.GetExtension(picked)))
                : doc.Encoding;
            return await WriteAsync(doc, picked, encoding);
        }

        if (doc.FilePath is null)
        {
            var result = await PromptUntitledFileNameAsync(doc, askDontSave: false);
            return result.Choice == SaveChoice.Save && await SaveUntitledAsync(doc, result.FileName!);
        }

        // 磁盘上的文件在打开后被别的程序改过：确认是否覆盖
        var diskTime = DocumentViewModel.TryGetWriteTime(doc.FilePath);
        if (diskTime is not null && doc.KnownWriteTimeUtc is not null && diskTime != doc.KnownWriteTimeUtc &&
            !await Dialogs.ConfirmAsync(this, "文件已在外部被修改",
                $"“{doc.Title}”在打开后被其他程序修改过。保存将覆盖那些修改。", "覆盖保存"))
            return false;

        return await WriteAsync(doc, doc.FilePath, doc.Encoding);
    }

    /// <summary>
    /// 未命名文档的文件名对话框。默认文件名取首行文字（没有则用时间），后缀为默认后缀，都可以修改。
    /// <paramref name="askDontSave"/> 为真时（关闭标签页 / 窗口时）多一个“不保存”按钮。
    /// </summary>
    private Task<FileNamePromptResult> PromptUntitledFileNameAsync(DocumentViewModel doc, bool askDontSave)
    {
        var library = LibraryPath;
        return Dialogs.PromptFileNameAsync(this, new FileNamePrompt(
            Title: askDontSave ? "保存更改" : "保存到笔记库",
            BaseName: FileNaming.SuggestBaseName(doc.Document.Text, DateTime.Now),
            Extension: doc.NewFileExtension,
            PrimaryText: "保存",
            Validate: name => ValidateNewFileName(library, name))
        {
            Message = askDontSave ? $"是否将“{doc.Title}”保存到笔记库？" : null,
            Location = library,
            SecondaryText = askDontSave ? "不保存" : null,
        });
    }

    private static string? ValidateNewFileName(string directory, string fileName)
    {
        if (FileNaming.ValidateUserFileName(fileName) is { } error) return error;
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target) || Directory.Exists(target)) return "笔记库中已有同名文件。";
        return null;
    }

    private async Task<bool> SaveUntitledAsync(DocumentViewModel doc, string fileName)
    {
        Directory.CreateDirectory(LibraryPath);
        var path = Path.Combine(LibraryPath, fileName);
        if (File.Exists(path))
        {
            await Dialogs.ShowErrorAsync(this, "无法保存", $"已存在同名文件：{fileName}");
            return false;
        }
        return await WriteAsync(doc, path, TextEncoding.Utf8(Settings.ShouldWriteBom(Path.GetExtension(path))));
    }

    /// <summary>写入磁盘并处理错误（编码无法表示的字符、权限、磁盘错误）。</summary>
    private async Task<bool> WriteAsync(DocumentViewModel doc, string path, TextEncoding encoding)
    {
        try
        {
            doc.SaveTo(path, encoding);
            return true;
        }
        catch (EncodingLossException ex)
        {
            if (!await Dialogs.ConfirmAsync(this, "编码无法保存全部字符",
                    $"{ex.TargetEncoding.DisplayName} 编码无法保存字符“{ex.Character}”。\n\n" +
                    "改用 UTF-8 保存可以保留所有字符（推荐）。", "改用 UTF-8 保存"))
                return false;
            return await WriteAsync(doc, path, TextEncoding.Utf8(Settings.ShouldWriteBom(Path.GetExtension(path))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "保存失败", $"{path}\n\n{e.Message}\n\n可以尝试“另存为”到其他位置。");
            return false;
        }
    }

    private async Task<string?> PickSavePathAsync(DocumentViewModel doc)
    {
        var dir = doc.FilePath is { } p ? Path.GetDirectoryName(p)! : LibraryPath;
        Directory.CreateDirectory(dir);
        var name = doc.FilePath is { } fp
            ? Path.GetFileName(fp)
            : FileNaming.SuggestBaseName(doc.Document.Text, DateTime.Now) + doc.NewFileExtension;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            SuggestedFileName = name,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir),
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("所有文件") { Patterns = ["*"] },
                new FilePickerFileType("文本文档") { Patterns = ["*.txt"] },
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    private async Task SaveAllAsync()
    {
        foreach (var tab in AllTabs().Where(t => t.Doc.IsDirty).ToList())
        {
            Select(tab);
            if (!await SaveAsync(tab)) return;
        }
    }

    // ───────────────────────── 重命名 / 更改后缀 ─────────────────────────

    private async Task RenameAsync(DocumentTab tab)
    {
        var doc = tab.Doc;
        if (doc.FilePath is null)
        {
            // 未命名文档：重命名 = 用这个名字保存到笔记库
            var result = await PromptUntitledFileNameAsync(doc, askDontSave: false);
            if (result.Choice == SaveChoice.Save) await SaveUntitledAsync(doc, result.FileName!);
            return;
        }

        var oldPath = doc.FilePath;
        var dir = Path.GetDirectoryName(oldPath)!;
        var prompt = await Dialogs.PromptFileNameAsync(this, new FileNamePrompt(
            Title: "重命名",
            BaseName: Path.GetFileNameWithoutExtension(oldPath),
            Extension: Path.GetExtension(oldPath),
            PrimaryText: "重命名",
            Validate: name => ValidateRename(oldPath, name))
        {
            Location = dir,
        });
        if (prompt.Choice != SaveChoice.Save) return;
        await MoveFileAsync(tab, Path.Combine(dir, prompt.FileName!));
    }

    private static string? ValidateRename(string oldPath, string newName)
    {
        if (FileNaming.ValidateUserFileName(newName) is { } error) return error;
        var target = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        bool sameFile = string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && (File.Exists(target) || Directory.Exists(target))) return "已存在同名文件。";
        return null;
    }

    /// <summary>
    /// 更改后缀（“转换”为其他类型的文件）。已保存的文件会在磁盘上重命名；
    /// UTF-8 文件的 BOM 按规则调整（.txt/.md 带 BOM，代码文件不带）。
    /// </summary>
    internal async Task ChangeExtensionAsync(DocumentTab tab, string extension)
    {
        var doc = tab.Doc;
        extension = FileTypes.NormalizeExtension(extension);

        if (doc.FilePath is null)
        {
            doc.NewFileExtension = extension;
            if (doc.Encoding.CodePage == 65001)
                doc.Encoding = TextEncoding.Utf8(Settings.ShouldWriteBom(extension));
            return;
        }

        var oldPath = doc.FilePath;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, Path.GetFileNameWithoutExtension(oldPath) + extension);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        if (ValidateRename(oldPath, Path.GetFileName(newPath)) is { } error)
        {
            await Dialogs.ShowErrorAsync(this, "无法更改后缀", error);
            return;
        }
        if (!await MoveFileAsync(tab, newPath)) return;

        bool wantBom = Settings.ShouldWriteBom(extension);
        if (doc.Encoding.CodePage == 65001 && doc.Encoding.HasBom != wantBom)
        {
            bool wasDirty = doc.IsDirty;
            doc.ChangeEncoding(TextEncoding.Utf8(wantBom));
            // 文件没有其他未保存的修改时，立即写回，保证磁盘上的 BOM 与新类型一致
            if (!wasDirty) await WriteAsync(doc, newPath, doc.Encoding);
        }
    }

    private async Task<bool> MoveFileAsync(DocumentTab tab, string newPath)
    {
        var doc = tab.Doc;
        if (doc.FilePath is null || newPath == doc.FilePath) return true;
        try
        {
            File.Move(doc.FilePath, newPath);
            doc.UpdatePath(newPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "重命名失败", e.Message);
            return false;
        }
    }

    // ───────────────────────── 关闭 ─────────────────────────

    /// <summary>
    /// 关闭前询问是否保存。返回 Save（已保存）、DontSave 或 Cancel（取消或保存失败）。
    /// 未命名文档直接显示带文件名的对话框（保存 / 不保存 / 取消）。
    /// </summary>
    private async Task<SaveChoice> AskToSaveBeforeClosingAsync(DocumentTab tab)
    {
        var doc = tab.Doc;
        if (!doc.IsDirty) return SaveChoice.DontSave;
        Select(tab);

        if (doc.FilePath is null)
        {
            var result = await PromptUntitledFileNameAsync(doc, askDontSave: true);
            if (result.Choice != SaveChoice.Save) return result.Choice;
            return await SaveUntitledAsync(doc, result.FileName!) ? SaveChoice.Save : SaveChoice.Cancel;
        }

        var choice = await Dialogs.ConfirmSaveAsync(this, doc.Title);
        if (choice != SaveChoice.Save) return choice;
        return await SaveAsync(tab) ? SaveChoice.Save : SaveChoice.Cancel;
    }

    internal async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (Dialogs.IsOpen) return false;
        if (await AskToSaveBeforeClosingAsync(tab) == SaveChoice.Cancel) return false;
        RemoveTab(tab);
        return true;
    }

    private async Task CloseOthersAsync(DocumentTab keep)
    {
        foreach (var t in AllTabs().Where(t => t != keep).ToList())
            if (!await CloseTabAsync(t)) return;
        Select(keep);
    }

    private async Task CloseToTheRightAsync(DocumentTab tab)
    {
        var tabs = AllTabs();
        foreach (var t in tabs.Skip(tabs.IndexOf(tab) + 1).ToList())
            if (!await CloseTabAsync(t)) return;
    }

    // ───────────────────────── 外部修改 ─────────────────────────

    /// <summary>窗口重新激活 / 切换标签时，检查文件是否被其他程序修改或删除。</summary>
    internal void CheckExternalChanges(DocumentTab? only = null)
    {
        if (Dialogs.IsOpen) return;
        foreach (var tab in only is null ? AllTabs() : [only])
        {
            var doc = tab.Doc;
            if (doc.FilePath is not { } path) continue;
            var diskTime = DocumentViewModel.TryGetWriteTime(path);

            if (diskTime is null)
            {
                if (doc.KnownWriteTimeUtc is null) continue; // 已经提示过
                doc.MarkMissingOnDisk();
                doc.Notice = new DocumentNotice("文件已被删除或移动",
                    "编辑器中的内容仍然保留。按 Ctrl+S 可重新保存到原位置。", NoticeSeverity.Warning, []);
                continue;
            }

            if (doc.KnownWriteTimeUtc is null || diskTime == doc.KnownWriteTimeUtc) continue;

            if (!doc.IsDirty)
            {
                int caret = tab.Editor.CaretOffset;
                try { doc.Reload(); }
                catch (Exception) { continue; }
                tab.Editor.CaretOffset = Math.Min(caret, doc.Document.TextLength);
                ShowTransientNotice(doc, new DocumentNotice("已重新加载", "文件已被其他程序修改，已载入最新内容（可按 Ctrl+Z 撤销）。",
                    NoticeSeverity.Informational, []));
            }
            else
            {
                // 只提示一次：记住磁盘上的新时间，之后保存时会再确认一次是否覆盖
                var seen = diskTime;
                doc.Notice = new DocumentNotice("文件已在外部被修改",
                    "你在编辑器中也有未保存的更改。",
                    NoticeSeverity.Warning,
                    [
                        new NoticeAction("载入磁盘上的版本", () =>
                        {
                            try { doc.Reload(); doc.Notice = null; }
                            catch (Exception e) { _ = Dialogs.ShowErrorAsync(this, "无法重新加载", e.Message); }
                        }),
                        new NoticeAction("保留我的版本", () =>
                        {
                            doc.KnownWriteTimeUtc = seen;
                            doc.Notice = null;
                        }),
                    ]);
            }
        }
    }

    // ───────────────────────── 编码 / 换行符 ─────────────────────────

    private async Task ReopenWithEncodingAsync(DocumentViewModel doc, TextEncoding encoding)
    {
        if (doc.FilePath is null) return;
        if (doc.IsDirty && !await Dialogs.ConfirmAsync(this, "重新打开",
                "以其他编码重新打开会丢弃未保存的更改（可按 Ctrl+Z 撤销）。", "重新打开"))
            return;
        try
        {
            doc.Reload(encoding);
            doc.Notice = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "无法重新打开", e.Message);
        }
    }

    private async Task SaveWithEncodingAsync(DocumentTab tab, TextEncoding encoding)
    {
        var old = tab.Doc.Encoding;
        tab.Doc.ChangeEncoding(encoding);
        if (!await SaveAsync(tab))
            tab.Doc.Encoding = old; // 保存失败或取消：恢复原编码显示
    }

    // ───────────────────────── 拖放 ─────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return;
        e.Handled = true;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) _ = OpenFilesAsync(paths);
    }
}
