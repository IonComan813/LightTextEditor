using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LightTextEditor.App.Services;
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

    /// <summary>
    /// 新文件 / 改用 UTF-8 时的 UTF-8 形式：<c>.editorconfig</c> 指定了 utf-8 或 utf-8-bom 时按它，
    /// 否则 .txt/.md 带 BOM、代码文件不带（见设置）。
    /// </summary>
    private static TextEncoding PreferredUtf8(string path) =>
        EditorConfig.GetEncoding(path) is { IsUtf8: true } configured
            ? configured
            : TextEncoding.Utf8(Settings.ShouldWriteBom(Path.GetExtension(path)));

    /// <summary>新文件保存时使用的编码：<c>.editorconfig</c> 的 charset 优先，否则为 <see cref="PreferredUtf8"/>。</summary>
    private static TextEncoding DefaultEncodingFor(string path) =>
        EditorConfig.GetEncoding(path) ?? PreferredUtf8(path);

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
        // 快捷方式 / 符号链接：打开它指向的文件
        path = LibraryShortcuts.ResolveForOpening(path);
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

            var loaded = TextFileIO.Load(path, Settings.NewFileLineEnding, App.EncodingMemory.Get(path));
            if (loaded.LooksBinary &&
                !await Dialogs.ConfirmAsync(this, "可能不是文本文件",
                    $"“{info.Name}”看起来是二进制文件，以文本方式打开并保存可能会损坏它。仍要打开吗？", "打开"))
                return null;

            var tab = AddLoadedDocument(path, loaded);
            // 笔记库以外的文件：在笔记库里放一个快捷方式（文件仍保存在原位置）
            LibraryShortcuts.Ensure(LibraryPath, path);
            return tab;
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
            path = LibraryShortcuts.ResolveForOpening(path);
            if (FindTab(path) is { } existing) return existing;
            if (new FileInfo(path).Length > LargeFileThreshold) return null;
            var loaded = TextFileIO.Load(path, Settings.NewFileLineEnding, App.EncodingMemory.Get(path));
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
        var tab = AddTab(doc, select); // 总是在新标签页中打开（当前的空白标签页保留）
        // 编码待确认的提示条常驻：被其他提示替换、或保存后，只要还没点 ✓ 就重新显示
        doc.StandingNotice = () => doc.Encoding.IsCertain ? null : EncodingNotice(tab);
        doc.Notice = EncodingNotice(tab);
        if (doc.Notice is null && loaded.HasMixedLineEndings)
            doc.Notice = new DocumentNotice("换行符不一致",
                $"此文件混用了多种换行符，主要为 {loaded.LineEnding.DisplayName()}。可在状态栏统一。",
                NoticeSeverity.Informational, []);
        return tab;
    }

    /// <summary>
    /// 编码"待确认"时的常驻提示条：[更换编码重试▾] [✓]，不能关闭，直到用户点 ✓（见 <see cref="ConfirmEncoding"/>，
    /// 之后再询问是否转换为 UTF-8）。编码确定但有无法识别的字节时显示可关闭的警告。不需要提示时返回 null。
    /// </summary>
    private DocumentNotice? EncodingNotice(DocumentTab tab)
    {
        var doc = tab.Doc;
        var detection = doc.Detection;
        bool uncertain = !doc.Encoding.IsCertain;
        if (!uncertain && !doc.HasUndecodableBytes) return null;

        var enc = doc.Encoding.Description;
        const string hint = "显示正常请点 ✓，出现乱码请更换编码重试。";
        string message = !uncertain
            ? $"有部分字节无法按 {enc} 识别（显示为 �），按此编码保存会改变它们。可以更换编码重试。"
            : (doc.HasSwitchedEncoding
                ? $"已按 {enc} 重新打开。显示正常请点 ✓。"
                : detection?.Method switch
                {
                    DetectionMethod.Statistical when detection.ConfidenceText is { } c => $"检测为 {enc}（置信度 {c}）。{hint}",
                    DetectionMethod.CjkHeuristic => $"推测为 {enc}。{hint}",
                    _ => $"无法判断编码，暂按 {enc} 显示。{hint}",
                })
              + (doc.HasUndecodableBytes ? "\n有部分字节无法按此编码识别（显示为 �）。" : "");

        // 第一次显示"更换编码重试"；用户换过一次编码后，显示当前编码名
        var actions = new List<NoticeAction>
        {
            new(doc.HasSwitchedEncoding ? enc : "更换编码重试", () => { }) { Children = ReopenChoices(doc, dimCurrent: true) },
        };
        if (uncertain)
            actions.Add(new NoticeAction("显示正常", () => ConfirmEncoding(doc)) { Icon = NoticeIcon.Checkmark });

        return uncertain
            ? new DocumentNotice("请确认当前文件的编码格式", message, NoticeSeverity.Warning, actions) { IsStanding = true }
            : new DocumentNotice("有无法识别的字节", message, NoticeSeverity.Warning, actions);
    }

    /// <summary>
    /// "更换编码重试"的选项：常用编码，再加上检测时考虑过、但不在常用列表里的编码。当前编码打勾；
    /// 这次打开以来试过的（包括打开前检测考虑过的）显示为灰色，仍可选择。
    /// <paramref name="dimCurrent"/> 为 false 时当前编码不变灰（顶部菜单、状态栏菜单）。
    /// </summary>
    private IReadOnlyList<NoticeAction> ReopenChoices(DocumentViewModel doc, bool dimCurrent)
    {
        var choices = ReopenEncodings.Select(e => (e.Name, e.Create)).ToList();
        foreach (var cp in doc.TriedCodePages)
        {
            if (choices.Any(c => c.Create().CodePage == cp)) continue;
            TextEncoding extra;
            try { extra = TextEncoding.FromCodePage(cp); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { continue; }
            choices.Add((extra.Description, () => extra));
        }

        return
        [
            .. choices.Select(c =>
            {
                int codePage = c.Create().CodePage;
                bool isCurrent = codePage == doc.Encoding.CodePage;
                return new NoticeAction(c.Name, () => _ = ReopenWithEncodingAsync(doc, c.Create()))
                {
                    IsChecked = isCurrent,
                    IsDimmed = doc.TriedCodePages.Contains(codePage) && (dimCurrent || !isCurrent),
                };
            }),
        ];
    }

    /// <summary>
    /// 用户确认当前编码显示正常：记入编码记录（下次直接使用，不改动文件），
    /// 然后在提示条上询问是否转换为 UTF-8。
    /// </summary>
    private void ConfirmEncoding(DocumentViewModel doc)
    {
        doc.Encoding = doc.Encoding with { IsCertain = true };
        if (doc.FilePath is { } path) App.EncodingMemory.Remember(path, doc.Encoding);
        doc.Notice = doc.FilePath is not null && !doc.Encoding.IsUtf8 && !doc.HasUndecodableBytes &&
                     FindTabByDocument(doc) is { } tab
            ? ConvertOffer(tab)
            : null;
    }

    private DocumentNotice ConvertOffer(DocumentTab tab)
    {
        var doc = tab.Doc;
        return new DocumentNotice("编码已确认",
            $"以后打开这个文件会直接使用 {doc.Encoding.DisplayName}。要转换为 UTF-8 吗？转换后其他软件也能正确识别。",
            NoticeSeverity.Informational,
            [
                new NoticeAction("转换为 UTF-8", () => _ = ConvertToUtf8Async(tab)),
                new NoticeAction($"保持 {doc.Encoding.DisplayName}", () => doc.Notice = null),
            ]);
    }

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
            return picked is not null && await SaveAsToAsync(tab, picked);
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
    /// 另存为指定路径。保存到笔记库以外时，在笔记库的“外部文件”文件夹里放一个指向新文件的快捷方式
    /// （从笔记库另存出去的文件也能在笔记库里找到）。
    /// </summary>
    internal async Task<bool> SaveAsToAsync(DocumentTab tab, string path)
    {
        var doc = tab.Doc;
        var encoding = doc.IsUntitled ? DefaultEncodingFor(path) : doc.Encoding;
        if (!await WriteAsync(doc, path, encoding)) return false;
        if (LibraryShortcuts.Ensure(LibraryPath, path) is not null)
            ShowTransientNotice(doc, new DocumentNotice("已另存为",
                $"{path}\n笔记库的“{LibraryShortcuts.FolderName}”文件夹里有指向它的快捷方式。", NoticeSeverity.Success, []));
        return true;
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
        return await WriteAsync(doc, path, DefaultEncodingFor(path));
    }

    /// <summary>写入磁盘并处理错误（编码无法表示的字符、权限、磁盘错误）。</summary>
    private async Task<bool> WriteAsync(DocumentViewModel doc, string path, TextEncoding encoding, bool undecodableConfirmed = false)
    {
        if (doc.HasUndecodableBytes && !undecodableConfirmed && !await Dialogs.ConfirmAsync(this, "有无法识别的字节",
                $"“{doc.Title}”中有部分字节无法按 {doc.Detection!.Encoding.DisplayName} 识别（显示为 �）。" +
                "保存后它们会被替换，原来的内容无法恢复。\n\n建议先在状态栏换一个编码重新打开。", "仍然保存"))
            return false;
        try
        {
            doc.SaveTo(path, encoding);
            // 编码记录：UTF-8 / 带 BOM 的文件能自我识别，不需要；其他编码记下来，下次打开不必再猜
            // 还在"待确认"的编码不记录（等用户点 ✓）
            if (doc.Encoding.IsUnicode) App.EncodingMemory.Forget(path);
            else if (doc.Encoding.IsCertain) App.EncodingMemory.Remember(path, doc.Encoding);
            return true;
        }
        catch (EncodingLossException ex)
        {
            if (!await Dialogs.ConfirmAsync(this, "编码无法保存全部字符",
                    $"{ex.TargetEncoding.DisplayName} 编码无法保存字符“{ex.Character}”。\n\n" +
                    "改用 UTF-8 保存可以保留所有字符（推荐）。", "改用 UTF-8 保存"))
                return false;
            return await WriteAsync(doc, path, PreferredUtf8(path), undecodableConfirmed: true);
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

        bool wantBom = PreferredUtf8(newPath).HasBom;
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
            var oldPath = doc.FilePath;
            File.Move(oldPath, newPath);
            doc.UpdatePath(newPath);
            App.EncodingMemory.Move(oldPath, newPath);
            // 外部文件改名后，笔记库里的快捷方式也跟着换
            if (!LibraryShortcuts.IsInsideLibrary(LibraryPath, oldPath))
            {
                LibraryShortcuts.Remove(LibraryPath, oldPath);
                LibraryShortcuts.Ensure(LibraryPath, newPath);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "重命名失败", e.Message);
            return false;
        }
    }

    // ───────────────────────── 移动到笔记库 ─────────────────────────

    internal bool IsExternalFile(DocumentViewModel doc) =>
        doc.FilePath is { } p && !LibraryShortcuts.IsInsideLibrary(LibraryPath, p);

    /// <summary>
    /// 把笔记库以外的文件移动到笔记库（确认后），并删掉它的快捷方式。
    /// Markdown 文件引用的本地图片一同迁移（见 <see cref="MarkdownImageMigration"/>）。
    /// </summary>
    internal async Task<bool> MoveToLibraryAsync(DocumentTab tab, bool confirm = true)
    {
        var doc = tab.Doc;
        if (doc.FilePath is not { } oldPath || !IsExternalFile(doc)) return false;

        Directory.CreateDirectory(LibraryPath);
        var target = Path.Combine(LibraryPath, Path.GetFileName(oldPath));
        if (File.Exists(target) || Directory.Exists(target))
            target = FileNaming.MakeUniquePath(LibraryPath, Path.GetFileNameWithoutExtension(oldPath), Path.GetExtension(oldPath));

        var images = IsMarkdown(doc)
            ? MarkdownImageMigration.Plan(doc.Document.Text, oldPath, LibraryPath)
            : new ImageMigrationPlan([], []);
        int copied = images.Transfers.Count(t => t.Copy);

        if (confirm && !await Dialogs.ConfirmAsync(this, "移动到笔记库",
                $"将“{Path.GetFileName(oldPath)}”移动到笔记库？\n\n" +
                $"从：{Path.GetDirectoryName(oldPath)}\n到：{LibraryPath}" +
                (Path.GetFileName(target) != Path.GetFileName(oldPath) ? $"\n（笔记库中已有同名文件，将保存为“{Path.GetFileName(target)}”）" : "") +
                (images.IsEmpty ? "" : $"\n\n文中引用的 {images.Transfers.Count} 张图片也会一起放进笔记库" +
                    (copied > 0 ? $"（其中 {copied} 张在原位置以外或也被其他笔记使用，复制一份）" : "") + "。") +
                "\n\n原位置将不再保留这个文件。", "移动"))
            return false;

        try
        {
            File.Move(oldPath, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "移动失败", e.Message);
            return false;
        }
        doc.UpdatePath(target);
        App.EncodingMemory.Move(oldPath, target);
        LibraryShortcuts.Remove(LibraryPath, oldPath);

        var failed = MarkdownImageMigration.Execute(images);
        var edits = images.Edits.Where(e => !failed.Contains(e.Target)).ToList();
        if (edits.Count > 0)
        {
            // 图片换了位置或名字：改写链接。文件原本没有未保存的修改时直接保存
            bool wasDirty = doc.IsDirty;
            doc.Document.BeginUpdate();
            foreach (var edit in edits.OrderByDescending(e => e.Start))
                doc.Document.Replace(edit.Start, edit.Length, edit.NewText);
            doc.Document.EndUpdate();
            if (!wasDirty) await WriteAsync(doc, target, doc.Encoding);
        }

        if (failed.Count > 0)
            await Dialogs.ShowErrorAsync(this, "部分图片未能迁移",
                $"{failed.Count} 张图片没能放进笔记库，文中仍然引用原来的位置。");
        else
            ShowTransientNotice(doc, new DocumentNotice("已移动到笔记库",
                target + (images.IsEmpty ? "" : $"（连同 {images.Transfers.Count} 张图片）"), NoticeSeverity.Success, []));
        return true;
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
        if (Dialogs.IsOpenFor(this)) return false;
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
        if (Dialogs.IsOpenFor(this)) return;
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
                try { doc.Reload(remembered: App.EncodingMemory.Get(path)); }
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
                            try { doc.Reload(remembered: App.EncodingMemory.Get(path)); doc.Notice = EncodingNotice(tab); }
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
            // 重新打开后仍是"待确认"：用户在提示条上点 ✓ 后才记入编码记录。
            // 从菜单更换编码重试时，之前关掉的提示条重新出现（又在试编码了）
            doc.Reload(encoding);
            doc.IsStandingNoticeDismissed = false;
            doc.Notice = FindTabByDocument(doc) is { } tab ? EncodingNotice(tab) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "无法重新打开", e.Message);
        }
    }

    private DocumentTab? FindTabByDocument(DocumentViewModel doc) => AllTabs().FirstOrDefault(t => t.Doc == doc);

    private async Task<bool> SaveWithEncodingAsync(DocumentTab tab, TextEncoding encoding)
    {
        var old = tab.Doc.Encoding;
        tab.Doc.ChangeEncoding(encoding);
        if (await SaveAsync(tab)) return true;
        tab.Doc.Encoding = old; // 保存失败或取消：恢复原编码显示
        return false;
    }

    /// <summary>
    /// 把文件转换为 UTF-8：按当前编码的解读重新以 UTF-8 写入（.txt/.md 带 BOM，代码文件不带；
    /// <c>.editorconfig</c> 指定时按它）。转换后不再需要编码记录。
    /// </summary>
    internal async Task<bool> ConvertToUtf8Async(DocumentTab tab)
    {
        var doc = tab.Doc;
        if (doc.FilePath is not { } path)
        {
            doc.ChangeEncoding(TextEncoding.Utf8(Settings.ShouldWriteBom(doc.Extension)));
            return true;
        }
        if (doc.HasUndecodableBytes)
        {
            await Dialogs.ShowErrorAsync(this, "无法转换",
                $"有部分字节无法按 {doc.Encoding.DisplayName} 识别，转换后它们会永久丢失。请先换一个编码重新打开。");
            return false;
        }
        if (!doc.Encoding.IsCertain && !await Dialogs.ConfirmAsync(this, "转换为 UTF-8",
                $"将按 {doc.Encoding.Description} 的解读把“{doc.Title}”转换为 UTF-8 并保存。\n\n" +
                "请先确认内容显示正常：如果现在是乱码，转换后就无法再恢复原文。", "转换"))
            return false;
        if (!await SaveWithEncodingAsync(tab, PreferredUtf8(path))) return false;
        ShowTransientNotice(doc, new DocumentNotice("已转换为 UTF-8", $"“{doc.Title}”已保存为 {doc.Encoding.DisplayName}。",
            NoticeSeverity.Success, []));
        return true;
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
        if (paths.Length == 0) return;

        // 图片：拖进 Markdown 文件就插入图片标签；其他文件照常打开
        var images = paths.Where(LightTextEditor.Core.Markdown.MarkdownAssets.IsImageFile).ToList();
        var others = paths.Except(images).ToArray();
        if (images.Count > 0 && _active is { } tab)
        {
            if (IsMarkdown(tab.Doc))
            {
                if (tab.IsPreviewing) SetPreview(tab, false);
                try { InsertImageFiles(tab, images); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ = Dialogs.ShowErrorAsync(this, "无法插入图片", ex.Message);
                }
            }
            else
            {
                ShowTransientNotice(tab.Doc, new DocumentNotice("图片只能插入 Markdown 文件",
                    "可以在状态栏把文件类型改为 Markdown (.md)。", NoticeSeverity.Informational, []), 6);
            }
        }
        if (others.Length > 0) _ = OpenFilesAsync(others);
    }
}
