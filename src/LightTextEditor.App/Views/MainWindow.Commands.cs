using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LightTextEditor.App.Services;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Views;

// 菜单命令、快捷键、状态栏菜单
public partial class MainWindow
{
    // ───────────────────────── 快捷键 ─────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Dialogs.IsOpenFor(this)) return;

        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);

        Func<Task>? action = (ctrl, shift, alt, e.Key) switch
        {
            (true, false, false, Key.N) => () => { NewDocument(); return Task.CompletedTask; },
            (true, false, false, Key.T) => () => { NewDocument(); return Task.CompletedTask; },
            (true, false, false, Key.O) => OpenWithPickerAsync,
            (true, false, false, Key.S) => () => SaveActiveAsync(),
            (true, true, false, Key.S) => () => SaveActiveAsync(saveAs: true),
            (true, false, true, Key.S) => SaveAllAsync,
            (true, false, false, Key.W) or (true, false, false, Key.F4) =>
                () => _active is null ? Task.CompletedTask : CloseTabAsync(_active),
            (true, false, false, Key.Tab) or (true, false, false, Key.PageDown) => Sync(() => SelectRelative(1)),
            (true, true, false, Key.Tab) or (true, false, false, Key.PageUp) => Sync(() => SelectRelative(-1)),
            (false, false, false, Key.F2) => () => _active is null ? Task.CompletedTask : RenameAsync(_active),
            (true, false, false, Key.G) => GoToLineAsync,
            (true, false, false, Key.E) => Sync(TogglePreview),
            (false, false, false, Key.Escape) when _active is { IsPreviewing: true } => Sync(TogglePreview),
            (true, false, false, Key.F) => Sync(() => OpenSearch(replace: false)),
            (true, false, false, Key.H) => Sync(() => OpenSearch(replace: true)),
            (false, false, false, Key.F3) => Sync(() => FindAgain(previous: false)),
            (false, true, false, Key.F3) => Sync(() => FindAgain(previous: true)),
            (false, false, false, Key.F5) => Sync(InsertDateTime),
            (true, false, false, Key.OemPlus or Key.Add) => Sync(() => SetZoom(_vm.Zoom + 0.1)),
            (true, false, false, Key.OemMinus or Key.Subtract) => Sync(() => SetZoom(_vm.Zoom - 0.1)),
            (true, false, false, Key.D0 or Key.NumPad0) => Sync(() => SetZoom(1.0)),
            (false, false, true, Key.Z) => Sync(() => _vm.IsWordWrapOn = !_vm.IsWordWrapOn), // Markdown 总是换行，不改设置
            (true, false, false, >= Key.D1 and <= Key.D9) => Sync(() => SelectByNumber(e.Key - Key.D0)),
            _ => null,
        };

        if (action is null) return;
        e.Handled = true;
        _ = action();
    }

    private static Func<Task> Sync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    /// <summary>Ctrl+1…8 选择对应标签页，Ctrl+9 选择最后一个（与浏览器一致）。</summary>
    private void SelectByNumber(int n)
    {
        var tabs = AllTabs();
        if (tabs.Count == 0) return;
        Select(n == 9 ? tabs[^1] : tabs[Math.Min(n, tabs.Count) - 1]);
    }

    // ───────────────────────── 文件菜单 ─────────────────────────

    private void OnNewClick(object? sender, RoutedEventArgs e) => NewDocument();
    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenWithPickerAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveActiveAsync();
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveActiveAsync(saveAs: true);
    private async void OnSaveAllClick(object? sender, RoutedEventArgs e) => await SaveAllAsync();

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_active is not null) await RenameAsync(_active);
    }

    private void OnFileMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source != FileMenu) return;
        MoveToLibraryMenuItem.IsEnabled = _active is not null && IsExternalFile(_active.Doc);
    }

    private async void OnMoveToLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (_active is not null) await MoveToLibraryAsync(_active);
    }

    private void OnOpenLibraryFolderClick(object? sender, RoutedEventArgs e) => Shell.OpenFolder(LibraryPath);

    /// <summary>
    /// 设为默认程序：先确保已在“默认应用”中登记（便携版或开发时运行的也能用），再打开 Windows 设置页让用户选择。
    /// </summary>
    private void OnDefaultAppsClick(object? sender, RoutedEventArgs e)
    {
        FileAssociations.TryRegister();
        FileAssociations.OpenDefaultAppsSettings();
    }

    private async void OnChangeLibraryClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LibraryPath);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择笔记库文件夹",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(LibraryPath),
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        Settings.LibraryPath = path;
        App.SaveSettings();
        if (_active is not null)
            ShowTransientNotice(_active.Doc, new ViewModels.DocumentNotice("笔记库位置已更改", path,
                ViewModels.NoticeSeverity.Success, []));
    }

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (_active is not null) await CloseTabAsync(_active);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    // ───────────────────────── 帮助菜单 ─────────────────────────

    /// <summary>打开笔记库里的 Markdown 示例（不存在时重新生成一份）。</summary>
    private async void OnOpenSampleClick(object? sender, RoutedEventArgs e) =>
        await OpenSampleAsync(() => SampleNote.Ensure(LibraryPath, Settings));

    /// <summary>打开笔记库里的 HTML 示例（不存在时重新生成一份）。</summary>
    private async void OnOpenHtmlSampleClick(object? sender, RoutedEventArgs e) =>
        await OpenSampleAsync(() => SampleNote.EnsureHtml(LibraryPath, Settings));

    private async Task OpenSampleAsync(Func<string> ensure)
    {
        try
        {
            await OpenFilesAsync([ensure()]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(this, "无法打开示例", ex.Message);
        }
    }

    /// <summary>用默认浏览器打开当前网页文件（有未保存的更改时先保存）。</summary>
    private async void OnOpenInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (_active is not { } tab) return;
        if ((tab.Doc.IsDirty || tab.Doc.FilePath is null) && !await SaveAsync(tab)) return;
        if (tab.Doc.FilePath is { } path) Shell.Open(path);
    }

    // ───────────────────────── 编辑菜单 ─────────────────────────

    private void OnEditMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, EditMenu)) return; // 子菜单打开时也会冒泡上来
        UndoMenuItem.IsEnabled = _active?.Editor.CanUndo == true;
        RedoMenuItem.IsEnabled = _active?.Editor.CanRedo == true;
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => _active?.Editor.Undo();
    private void OnRedoClick(object? sender, RoutedEventArgs e) => _active?.Editor.Redo();
    private void OnCutClick(object? sender, RoutedEventArgs e) => _active?.Editor.Cut();
    private void OnCopyClick(object? sender, RoutedEventArgs e) => _active?.Editor.Copy();
    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (_active is not null) await PasteAsync(_active);
    }
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => _active?.Editor.SelectAll();
    private void OnFindClick(object? sender, RoutedEventArgs e) => OpenSearch(replace: false);
    private void OnReplaceClick(object? sender, RoutedEventArgs e) => OpenSearch(replace: true);
    private async void OnGoToLineClick(object? sender, RoutedEventArgs e) => await GoToLineAsync();
    private void OnInsertDateTimeClick(object? sender, RoutedEventArgs e) => InsertDateTime();
    private void OnExpandClick(object? sender, RoutedEventArgs e) { if (_active is { } tab) Reformat(tab, expand: true); }
    private void OnCompactClick(object? sender, RoutedEventArgs e) { if (_active is { } tab) Reformat(tab, expand: false); }

    /// <summary>打开查找 / 替换面板。不用编辑器里选中的文字填充查找框（保留上次查找的内容）。</summary>
    private void OpenSearch(bool replace)
    {
        if (_active is null) return;
        if (_active.IsPreviewing) SetPreview(_active, false); // 查找在源码中进行
        FindPanel.Editor = _active.Editor;
        FindPanel.Open(replace);
    }

    /// <summary>F3 / Shift+F3：按查找框里的内容查找下一个 / 上一个；还没有查找内容时打开面板。</summary>
    private void FindAgain(bool previous)
    {
        if (_active is null) return;
        if (_active.IsPreviewing) SetPreview(_active, false);
        FindPanel.Editor = _active.Editor;
        if (string.IsNullOrEmpty(FindPanel.SearchPattern) || !FindPanel.IsOpen)
        {
            FindPanel.Open(replace: false);
            if (string.IsNullOrEmpty(FindPanel.SearchPattern)) return;
        }
        if (previous) FindPanel.FindPrevious(); else FindPanel.FindNext();
    }

    private async Task GoToLineAsync()
    {
        if (_active is null) return;
        var editor = _active.Editor;
        var line = await Dialogs.PromptLineNumberAsync(this, editor.TextArea.Caret.Line, editor.Document.LineCount);
        if (line is null) return;
        editor.TextArea.Caret.Line = line.Value;
        editor.TextArea.Caret.Column = 1;
        EditorScrolling.ScrollCaretIntoView(editor);
        FocusEditor();
    }

    /// <summary>
    /// 把整个文件展开为多行或压缩为单行（JSON、CSS、JavaScript，见 <see cref="CodeLayout"/>）。一次修改，可以撤销；
    /// 不支持的文件或无法可靠分析的内容提示原因，不改动。保留文件末尾的换行。
    /// </summary>
    internal bool Reformat(DocumentTab tab, bool expand)
    {
        if (tab.IsPreviewing) SetPreview(tab, false);
        var editor = tab.Editor;
        var document = editor.Document;
        var text = document.Text;
        var verb = expand ? "展开" : "压缩";

        if (CodeLayout.LanguageFor(tab.Doc.Extension) is not { } language)
        {
            ShowTransientNotice(tab.Doc, new DocumentNotice($"无法{verb}：不支持这种文件",
                "只能展开和压缩 JSON、CSS 和 JavaScript 文件（.json、.css、.js）。",
                NoticeSeverity.Warning, []), seconds: 8);
            return false;
        }

        var newLine = tab.Doc.LineEnding.ToNewLineString();
        var result = expand
            ? CodeLayout.Expand(language, text, editor.Options.IndentationString, newLine)
            : CodeLayout.Compact(language, text, newLine);
        if (result.Text is not { } formatted)
        {
            ShowTransientNotice(tab.Doc, new DocumentNotice($"无法{verb}这个 {CodeLayout.DisplayName(language)} 文件，没有改动",
                result.Error ?? "", NoticeSeverity.Warning, []), seconds: 8);
            return false;
        }

        if (text.EndsWith('\n') || text.EndsWith('\r')) formatted += newLine; // 保留文件末尾的换行
        if (formatted != text)
        {
            document.Replace(0, document.TextLength, formatted); // 一次修改，Ctrl+Z 撤销
            editor.CaretOffset = 0;
            editor.TextArea.Caret.BringCaretToView();
        }

        string title, message;
        if (formatted == text)
        {
            title = expand ? "已经是多行" : "已经是单行";
            message = "内容没有变化。";
        }
        else if (!expand && result.KeptLineBreaks > 0)
        {
            title = "已压缩";
            message = $"有 {result.KeptLineBreaks} 处换行会影响代码的含义（如省略了分号的语句之间、行注释之后），保留未动。按 Ctrl+Z 可以撤销；保存后才会写入文件。";
        }
        else
        {
            title = expand ? "已展开为多行" : "已压缩为单行";
            message = "按 Ctrl+Z 可以撤销；保存后才会写入文件。";
        }
        ShowTransientNotice(tab.Doc, new DocumentNotice(title, message, NoticeSeverity.Success, []), seconds: 6);
        FocusEditor();
        return true;
    }

    private void InsertDateTime()
    {
        if (_active is null) return;
        var editor = _active.Editor;
        var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, text);
        editor.CaretOffset = editor.SelectionStart + text.Length;
        FocusEditor();
    }

    // ───────────────────────── 查看菜单 ─────────────────────────

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => SetZoom(_vm.Zoom + 0.1);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => SetZoom(_vm.Zoom - 0.1);
    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string theme }) return;
        Settings.Theme = theme;
        App.ApplyTheme(theme);
        App.SaveSettings();
        UpdateThemeMenu();
    }

    // ───────────────────────── 状态栏：编码 / 换行符 ─────────────────────────

    private static readonly (string Name, Func<TextEncoding> Create)[] ReopenEncodings =
    [
        ("UTF-8", () => TextEncoding.Utf8(false)),
        ("GB18030（简体中文，兼容 GBK）", () => TextEncoding.Gb18030(true)),
        ("Big5（繁体中文）", () => Legacy(950)),
        ("Shift-JIS（日文）", () => Legacy(932)),
        ("EUC-KR（韩文）", () => Legacy(949)),
        ("EUC-JP（日文）", () => Legacy(51932)),
        ("Windows-1251（西里尔文）", () => Legacy(1251)),
        ("Windows-1252（西欧）", () => Legacy(1252)),
        ("UTF-16 LE", () => new TextEncoding(new UnicodeEncoding(false, false), true, true)),
        ("UTF-16 BE", () => new TextEncoding(new UnicodeEncoding(true, false), true, true)),
    ];

    private static readonly (string Name, Func<TextEncoding> Create)[] SaveEncodings =
    [
        ("UTF-8", () => TextEncoding.Utf8(false)),
        ("UTF-8 with BOM", () => TextEncoding.Utf8(true)),
        ("GB18030", () => TextEncoding.Gb18030(true)),
        ("UTF-16 LE", () => new TextEncoding(new UnicodeEncoding(false, false), true, true)),
        ("UTF-16 BE", () => new TextEncoding(new UnicodeEncoding(true, false), true, true)),
    ];

    private static TextEncoding Legacy(int codePage) => new(Encoding.GetEncoding(codePage), false, true);

    /// <summary>
    /// 编码菜单（顶部“编码”菜单和状态栏编码按钮共用）：确认当前编码、换一个编码重新打开（试过的显示为灰色）、
    /// 以其他编码保存、转换为 UTF-8。
    /// </summary>
    internal List<Control> BuildEncodingMenuItems(DocumentTab tab)
    {
        var doc = tab.Doc;
        var items = new List<Control>();

        if (!doc.Encoding.IsCertain)
        {
            var confirm = new MenuItem { Header = $"确认 {doc.Encoding.DisplayName} 显示正常" };
            confirm.Click += (_, _) => ConfirmEncoding(doc);
            items.Add(confirm);
            items.Add(new Separator());
        }

        var reopen = new MenuItem { Header = "以其他编码重新打开", IsEnabled = doc.FilePath is not null };
        foreach (var choice in ReopenChoices(doc, dimCurrent: false)) reopen.Items.Add(ChoiceMenuItem(choice));
        items.Add(reopen);

        var saveWith = new MenuItem { Header = "以其他编码保存" };
        foreach (var (name, create) in SaveEncodings)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => _ = SaveWithEncodingAsync(tab, create());
            saveWith.Items.Add(item);
        }
        items.Add(saveWith);

        items.Add(new Separator());
        var convert = new MenuItem
        {
            Header = doc.Encoding.IsUtf8 ? "转换为 UTF-8（已经是 UTF-8）" : "转换为 UTF-8",
            IsEnabled = !doc.Encoding.IsUtf8,
        };
        convert.Click += (_, _) => _ = ConvertToUtf8Async(tab);
        items.Add(convert);
        return items;
    }

    private void OnEncodingButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_active is null || sender is not Control anchor) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var item in BuildEncodingMenuItems(_active)) flyout.Items.Add(item);
        flyout.ShowAt(anchor);
    }

    /// <summary>顶部“编码”菜单：每次打开时按当前标签页重建。</summary>
    private void OnEncodingMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (_active is null || e.Source != EncodingMenu) return;
        EncodingMenu.Items.Clear();
        EncodingMenu.Items.Add(new MenuItem { Header = $"当前：{_vm.EncodingText}", IsEnabled = false });
        EncodingMenu.Items.Add(new Separator());
        foreach (var item in BuildEncodingMenuItems(_active)) EncodingMenu.Items.Add(item);
    }

    // ───────────────────────── 文件类型（后缀） ─────────────────────────

    /// <summary>“更改后缀”菜单项：常用类型 + 其他后缀。</summary>
    private List<Control> BuildExtensionMenuItems(DocumentTab tab)
    {
        var current = tab.Doc.Extension;
        var items = new List<Control>();

        MenuItem Item(LightTextEditor.Core.Library.FileTypes.FileType t)
        {
            var item = new MenuItem
            {
                Header = t.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = string.Equals(t.Extension, current, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => _ = ChangeExtensionAsync(tab, t.Extension);
            return item;
        }

        var common = LightTextEditor.Core.Library.FileTypes.Common;
        // 前两项（纯文本、Markdown）直接列出，其余放进“代码 / 数据”子菜单
        items.Add(Item(common[0]));
        items.Add(Item(common[1]));
        var more = new MenuItem { Header = "代码 / 数据文件" };
        foreach (var t in common.Skip(2)) more.Items.Add(Item(t));
        items.Add(more);
        items.Add(new Separator());

        var other = new MenuItem { Header = "其他后缀…" };
        other.Click += async (_, _) =>
        {
            var input = await Dialogs.PromptTextAsync(this, "更改后缀", "新的后缀（例如 .toml）", current,
                text =>
                {
                    var ext = LightTextEditor.Core.Library.FileTypes.NormalizeExtension(text);
                    if (ext.Length == 0) return "请输入后缀。";
                    return LightTextEditor.Core.Library.FileNaming.ValidateUserFileName("a" + ext) is null
                        ? null : "后缀中包含不允许的字符。";
                });
            if (input is not null) await ChangeExtensionAsync(tab, input);
        };
        items.Add(other);
        return items;
    }

    private void OnFileTypeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_active is null || sender is not Control anchor) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var item in BuildExtensionMenuItems(_active)) flyout.Items.Add(item);
        flyout.ShowAt(anchor);
    }

    private void OnChangeExtensionMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (_active is null || e.Source != ChangeExtensionMenu) return;
        ChangeExtensionMenu.Items.Clear();
        foreach (var item in BuildExtensionMenuItems(_active)) ChangeExtensionMenu.Items.Add(item);
    }

    private void OnLineEndingButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_active is null || sender is not Control anchor) return;
        var doc = _active.Doc;
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var ending in new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.Cr })
        {
            var item = new MenuItem
            {
                Header = ending.DisplayName(),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = doc.LineEnding == ending,
            };
            item.Click += (_, _) =>
            {
                doc.ChangeLineEnding(ending);
                doc.Notice = null;
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }
}
