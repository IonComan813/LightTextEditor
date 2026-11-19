using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using FluentAvalonia.UI.Controls;
using LightTextEditor.App.Services;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;
using Avalonia.Controls.Chrome;
using Avalonia.VisualTree;

namespace LightTextEditor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<DocumentTab> _tabs = [];
    private DocumentTab? _active;
    internal DocumentTab? ActiveTab => _active;
    private bool _closeConfirmed;

    private static AppSettings Settings => App.Settings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.WordWrap = Settings.WordWrap;
        _vm.ShowLineNumbers = Settings.ShowLineNumbers;
        _vm.ShowStatusBar = Settings.ShowStatusBar;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 标题栏拖动区域（Avalonia 12：标记为 TitleBar 的元素可以拖动窗口、双击最大化）
        WindowDecorationProperties.SetElementRole(TitleBarDragArea, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(TabStripFooterDragArea, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(TabStripLeftInset, WindowDecorationsElementRole.TitleBar);
        if (OperatingSystem.IsMacOS())
        {
            // macOS 的红绿灯按钮在左侧
            TabStripLeftInset.Padding = new Thickness(76, 0, 8, 0);
            TabStripFooterDragArea.MinWidth = 40;
        }

        Tabs.TabItemsSource = _tabs;
        Tabs.TabItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DocumentTab>(
            (tab, _) => tab is null ? null! : new TabHeaderView(tab, CloseTabFromButton));
        // 标签上的鼠标操作：中键关闭、双击重命名、右键菜单
        Tabs.AddHandler(PointerReleasedEvent, OnTabMiddleClick, RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(DoubleTappedEvent, OnTabDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(ContextRequestedEvent, OnTabContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.SelectionChanged += (_, _) => OnSelectedTabChanged();
        Tabs.AddTabButtonClick += (_, _) => NewDocument();
        NoticeBar.Closed += (_, args) =>
        {
            if (args.Reason == FAInfoBarCloseReason.CloseButton && _active is not null)
                _active.Doc.Notice = null;
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        Activated += (_, _) => CheckExternalChanges();
        ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var t in _tabs)
            {
                if (t.Highlighting is not null) SyntaxHighlighting.ApplyTheme(t.Highlighting);
                ApplySelectionColors(t.Editor);
            }
        };
        Opened += (_, _) => UpdateBackdrop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) UpdateBackdrop();
        };

        UpdateThemeMenu();
    }

    /// <summary>启动时调用：恢复窗口位置、上次的标签页，并打开命令行传入的文件。</summary>
    public void Initialize(string[] args)
    {
        RestoreWindowPlacement();

        if (Settings.RestoreSession)
        {
            var session = JsonStore.LoadSession();
            var restored = new List<DocumentTab>();
            foreach (var f in session.Files)
            {
                if (!File.Exists(f.Path)) continue;
                var tab = TryOpenFileSilently(f.Path);
                if (tab is null) continue;
                tab.Doc.CaretOffset = f.CaretOffset;
                restored.Add(tab);
            }
            if (restored.Count > 0)
                Select(restored[Math.Clamp(session.SelectedIndex, 0, restored.Count - 1)]);
        }

        var files = args.Where(a => !a.StartsWith('-')).ToArray();

        // 第一次启动：在笔记库里放一份 Markdown 示例并打开
        if (!Settings.SampleNoteCreated)
        {
            try
            {
                var sample = SampleNote.Ensure(LibraryPath, Settings);
                Settings.SampleNoteCreated = true;
                App.SaveSettings();
                if (files.Length == 0) files = [sample];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 笔记库不可写时跳过，不影响启动
            }
        }

        if (files.Length > 0)
            _ = OpenFilesAsync(files);
        else if (AllTabs().Count == 0)
            NewDocument();
    }

    /// <summary>另一个实例转发过来的命令行参数。</summary>
    public void HandleExternalArguments(string[] args)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 部分系统会阻止后台程序抢焦点，临时置顶一下
        Topmost = true;
        Topmost = false;

        var files = args.Where(a => !a.StartsWith('-')).ToArray();
        if (files.Length > 0) _ = OpenFilesAsync(files);
    }

    // ───────────────────────── 标签页管理 ─────────────────────────

    internal List<DocumentTab> AllTabs() => _tabs.ToList();

    private async void CloseTabFromButton(DocumentTab tab) => await CloseTabAsync(tab);

    /// <summary>鼠标事件源所在的标签（不在任何标签上时为 null）。</summary>
    private static DocumentTab? TabFromSource(object? source) =>
        (source as Visual)?.FindAncestorOfType<FATabViewItem>(includeSelf: true)?.Header as DocumentTab;

    private static bool IsOnCloseButton(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } b && b.Classes.Contains("tabClose");

    private async void OnTabMiddleClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || TabFromSource(e.Source) is not { } tab) return;
        e.Handled = true;
        await CloseTabAsync(tab);
    }

    private async void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsOnCloseButton(e.Source) || TabFromSource(e.Source) is not { } tab) return;
        e.Handled = true;
        await RenameAsync(tab);
    }

    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TabFromSource(e.Source) is not { } tab || e.Source is not Control target) return;
        e.Handled = true;
        CreateTabContextMenu(tab).Open(target);
    }

    private DocumentTab AddTab(DocumentViewModel doc, bool select = true)
    {
        var editor = CreateEditor(doc);
        var tab = new DocumentTab(doc, editor);
        tab.Highlighting = SyntaxHighlighting.Install(editor, doc.Extension);
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.Notice) && tab == _active) UpdateNoticeBar();
            if (e.PropertyName == nameof(DocumentViewModel.Extension))
            {
                if (tab.Highlighting is not null) SyntaxHighlighting.ApplyLanguage(tab.Highlighting, doc.Extension);
                if (tab.IsPreviewing && !IsMarkdown(doc)) SetPreview(tab, false);
            }
        };
        // Ctrl+V / Shift+Insert：Markdown 文件中可以粘贴图片
        editor.TextArea.AddHandler(KeyDownEvent, async (_, e) =>
        {
            bool paste = (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) ||
                         (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift);
            if (!paste || !IsMarkdown(doc)) return;
            e.Handled = true;
            await PasteAsync(tab);
        }, RoutingStrategies.Tunnel);

        EditorHost.Children.Add(editor);
        _tabs.Add(tab);
        if (select) Select(tab);
        return tab;
    }

    private void RemoveTab(DocumentTab tab)
    {
        var tabs = AllTabs();
        int index = tabs.IndexOf(tab);
        _tabs.Remove(tab);
        EditorHost.Children.Remove(tab.Editor);
        if (tab.Preview is not null) EditorHost.Children.Remove(tab.Preview);
        tab.Highlighting?.Dispose();

        if (tab == _active)
        {
            _active = null;
            var remaining = AllTabs();
            if (remaining.Count > 0)
                Select(remaining[Math.Clamp(index, 0, remaining.Count - 1)]);
        }

        // 关掉最后一个标签页后，留一个空白标签页，随时可以写
        if (AllTabs().Count == 0) NewDocument();
    }

    private void Select(DocumentTab tab)
    {
        if (!ReferenceEquals(Tabs.SelectedItem, tab))
            Tabs.SelectedItem = tab;
        OnSelectedTabChanged();
    }

    private void OnSelectedTabChanged()
    {
        var tab = Tabs.SelectedItem as DocumentTab;
        if (tab is null || tab == _active) return;

        if (_active is not null)
        {
            _active.Doc.CaretOffset = _active.Editor.CaretOffset;
            _active.Editor.IsVisible = false;
            if (_active.Preview is not null) _active.Preview.IsVisible = false;
        }

        _active = tab;
        ApplyViewOptions(tab.Editor); // 后台标签页在切到前台时才同步行号、换行、缩放
        if (tab.IsPreviewing && tab.Preview is not null)
        {
            tab.Preview.SetMarkdown(tab.Doc.Document.Text, DocumentDirectory(tab.Doc));
            tab.Preview.IsVisible = true;
        }
        else
        {
            tab.Editor.IsVisible = true;
        }
        _vm.ActiveDocument = tab.Doc;
        _vm.IsPreviewing = tab.IsPreviewing;
        UpdateCaretStatus();
        UpdateNoticeBar();
        if (tab.IsPreviewing) tab.Preview?.Focus(); else FocusEditor();
        CheckExternalChanges(tab);
    }

    private void SelectRelative(int delta)
    {
        var tabs = AllTabs();
        if (tabs.Count < 2 || _active is null) return;
        int i = tabs.IndexOf(_active);
        Select(tabs[((i + delta) % tabs.Count + tabs.Count) % tabs.Count]);
    }

    private void FocusEditor()
    {
        var editor = _active?.Editor;
        if (editor is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (editor.IsVisible) editor.TextArea.Focus();
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── 拖动标签排序 ─────────────────────────
    // FluentAvalonia 自带的拖放排序会让标签脱离界面（消失），这里自己实现：
    // 按住标签水平拖动，越过相邻标签中线时交换位置。

    private const double TabDragThreshold = 6;
    private DocumentTab? _dragTab;
    private Point _dragStart;
    private bool _isDraggingTab;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Tabs).Properties.IsLeftButtonPressed) return;
        // 按在关闭按钮上不拖动
        if (IsOnCloseButton(e.Source) || TabFromSource(e.Source) is not { } tab) return;
        _dragTab = tab;
        _dragStart = e.GetPosition(Tabs);
        _isDraggingTab = false;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null) return;
        if (!e.GetCurrentPoint(Tabs).Properties.IsLeftButtonPressed)
        {
            EndTabDrag();
            return;
        }

        var pos = e.GetPosition(Tabs);
        if (!_isDraggingTab)
        {
            if (Math.Abs(pos.X - _dragStart.X) < TabDragThreshold) return;
            _isDraggingTab = true;
            e.Pointer.Capture(Tabs);
            Select(_dragTab); // 拖哪个标签就选中哪个
        }

        // 目标位置 = 中线在鼠标左侧的其他标签数
        int from = _tabs.IndexOf(_dragTab);
        if (from < 0) { EndTabDrag(); return; }
        int to = 0;
        foreach (var other in _tabs)
        {
            if (other == _dragTab || other.Header is null) continue;
            var item = other.TabItem;
            var origin = item.TranslatePoint(default, Tabs);
            if (origin is null) continue;
            if (origin.Value.X + item.Bounds.Width / 2 < pos.X) to++;
        }
        if (to != from) MoveTab(from, to);
        e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool wasDragging = _isDraggingTab;
        EndTabDrag();
        if (wasDragging)
        {
            e.Handled = true;
            FocusEditor();
        }
    }

    private void EndTabDrag()
    {
        _dragTab = null;
        _isDraggingTab = false;
    }

    /// <summary>移动标签位置，保持选中的标签不变。</summary>
    internal void MoveTab(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _tabs.Count || to >= _tabs.Count) return;
        var selected = _active;
        _tabs.Move(from, to);
        if (selected is not null && !ReferenceEquals(Tabs.SelectedItem, selected))
            Tabs.SelectedItem = selected;
    }

    private ContextMenu CreateTabContextMenu(DocumentTab tab)
    {
        MenuItem Item(string header, Func<Task> action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += async (_, _) => await action();
            return item;
        }

        var copyPath = Item("复制文件路径", async () =>
        {
            if (tab.Doc.FilePath is { } p && Clipboard is { } cb) await cb.SetTextAsync(p);
        });
        var reveal = Item("在文件夹中显示", () =>
        {
            if (tab.Doc.FilePath is { } p) Services.Shell.RevealInFolder(p);
            return Task.CompletedTask;
        });

        var moveToLibrary = Item("移动到笔记库…", () => MoveToLibraryAsync(tab));
        var menu = new ContextMenu
        {
            Items =
            {
                Item("关闭", () => CloseTabAsync(tab), "Ctrl+W"),
                Item("关闭其他标签页", () => CloseOthersAsync(tab)),
                Item("关闭右侧标签页", () => CloseToTheRightAsync(tab)),
                new Separator(),
                Item("保存", () => SaveAsync(tab), "Ctrl+S"),
                Item("重命名…", () => RenameAsync(tab), "F2"),
                moveToLibrary,
                new Separator(),
                copyPath,
                reveal,
            },
        };
        menu.Opening += (_, _) =>
        {
            copyPath.IsEnabled = reveal.IsEnabled = tab.Doc.FilePath is not null;
            moveToLibrary.IsEnabled = IsExternalFile(tab.Doc);
        };
        return menu;
    }

    // ───────────────────────── 编辑器 ─────────────────────────

    private TextEditor CreateEditor(DocumentViewModel doc)
    {
        var editor = new TextEditor
        {
            Document = doc.Document,
            FontFamily = new FontFamily(Settings.EditorFontFamily),
            FontSize = Settings.EditorFontSize * _vm.Zoom,
            ShowLineNumbers = _vm.ShowLineNumbers,
            WordWrap = _vm.WordWrap,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            IsVisible = false,
        };

        var o = editor.Options;
        o.ConvertTabsToSpaces = Settings.ConvertTabsToSpaces;
        o.IndentationSize = Settings.TabSize;
        o.HighlightCurrentLine = Settings.HighlightCurrentLine;
        o.EnableHyperlinks = true;
        o.RequireControlModifierForHyperlinkClick = true;
        o.EnableEmailHyperlinks = false;
        o.AllowScrollBelowDocument = true;
        o.EnableImeSupport = true;

        ApplySelectionColors(editor);
        var textView = editor.TextArea.TextView;
        textView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
        textView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), 1);

        SearchPanel.Install(editor);

        // 正文左侧留白（行号关闭时也保留）
        editor.TextArea.LeftMargins.Add(new Border { Width = 10, Background = Brushes.Transparent });
        StyleLineNumberMargin(editor);

        editor.TextArea.Caret.PositionChanged += (_, _) => { if (editor == _active?.Editor) UpdateCaretStatus(); };
        editor.TextArea.SelectionChanged += (_, _) => { if (editor == _active?.Editor) UpdateCaretStatus(); };
        doc.Document.TextChanged += (_, _) => { if (editor == _active?.Editor) _vm.CharacterCount = doc.Document.TextLength; };

        // Ctrl + 滚轮缩放
        editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SetZoom(_vm.Zoom + (e.Delta.Y > 0 ? 0.1 : -0.1));
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // 第一次显示时恢复光标位置
        void OnFirstLayout(object? s, EventArgs e)
        {
            editor.LayoutUpdated -= OnFirstLayout;
            if (doc.CaretOffset > 0)
            {
                editor.CaretOffset = Math.Min(doc.CaretOffset, doc.Document.TextLength);
                editor.TextArea.Caret.BringCaretToView();
            }
        }
        editor.LayoutUpdated += OnFirstLayout;

        return editor;
    }

    /// <summary>
    /// 选中文字的背景：主题强调色的 30%，保留文字本身的颜色（默认的实心蓝底会盖住文字）。
    /// </summary>
    private void ApplySelectionColors(TextEditor editor)
    {
        var accent = Color.FromRgb(0x2F, 0x6F, 0xED);
        if (Application.Current?.TryGetResource("SystemAccentColor", ActualThemeVariant, out var value) == true && value is Color c)
            accent = c;
        var area = editor.TextArea;
        area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x4D, accent.R, accent.G, accent.B));
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 2;
    }

    /// <summary>
    /// 把视图选项（行号、自动换行、缩放）同步到编辑器。只对当前（可见的）编辑器调用：
    /// AvaloniaEdit 打开行号时会新建行号控件，若编辑器此时被隐藏，该控件未经测量就会被渲染，
    /// 字号为 0 导致崩溃。
    /// </summary>
    private void ApplyViewOptions(TextEditor editor)
    {
        if (editor.WordWrap != _vm.WordWrap) editor.WordWrap = _vm.WordWrap;
        var fontSize = Settings.EditorFontSize * _vm.Zoom;
        if (editor.FontSize != fontSize) editor.FontSize = fontSize;
        if (editor.ShowLineNumbers != _vm.ShowLineNumbers)
        {
            editor.ShowLineNumbers = _vm.ShowLineNumbers;
            StyleLineNumberMargin(editor);
        }
    }

    /// <summary>行号与正文之间不用分隔虚线，改为留白，更接近 Windows 11 记事本的观感。</summary>
    private static void StyleLineNumberMargin(TextEditor editor)
    {
        foreach (var margin in editor.TextArea.LeftMargins)
        {
            if (margin is AvaloniaEdit.Editing.LineNumberMargin numbers)
            {
                numbers.Margin = new Thickness(12, 0, 4, 0);
                // 保险：立即测量一次，让行号控件拿到字号（见 ApplyViewOptions 的说明）
                numbers.Measure(Size.Infinity);
            }
            else if (AvaloniaEdit.Editing.DottedLineMargin.IsDottedLineMargin(margin))
                margin.IsVisible = false;
        }
        editor.LineNumbersForeground = new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80));
    }

    private void UpdateCaretStatus()
    {
        if (_active is null) return;
        var caret = _active.Editor.TextArea.Caret;
        _vm.CaretLine = caret.Line;
        _vm.CaretColumn = caret.Column;
        _vm.SelectionLength = _active.Editor.SelectionLength;
        _vm.CharacterCount = _active.Doc.Document.TextLength;
    }

    private void SetZoom(double zoom)
    {
        _vm.Zoom = Math.Round(Math.Clamp(zoom, 0.5, 4.0), 1);
        if (_active is not null) ApplyViewOptions(_active.Editor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.WordWrap):
                if (_active is not null) ApplyViewOptions(_active.Editor);
                Settings.WordWrap = _vm.WordWrap;
                App.SaveSettings();
                break;
            case nameof(MainViewModel.ShowLineNumbers):
                if (_active is not null) ApplyViewOptions(_active.Editor);
                Settings.ShowLineNumbers = _vm.ShowLineNumbers;
                App.SaveSettings();
                break;
            case nameof(MainViewModel.ShowStatusBar):
                Settings.ShowStatusBar = _vm.ShowStatusBar;
                App.SaveSettings();
                break;
        }
    }

    // ───────────────────────── 提示条 ─────────────────────────

    private void UpdateNoticeBar()
    {
        var notice = _active?.Doc.Notice;
        if (notice is null)
        {
            NoticeBar.IsOpen = false;
            return;
        }

        NoticeBar.Title = notice.Title;
        NoticeBar.Message = notice.Message;
        NoticeBar.Severity = notice.Severity switch
        {
            NoticeSeverity.Success => FAInfoBarSeverity.Success,
            NoticeSeverity.Warning => FAInfoBarSeverity.Warning,
            NoticeSeverity.Error => FAInfoBarSeverity.Error,
            _ => FAInfoBarSeverity.Informational,
        };

        if (notice.Actions.Count > 0)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var action in notice.Actions)
            {
                var button = new Button { Content = action.Label };
                button.Click += (_, _) => action.Execute();
                panel.Children.Add(button);
            }
            NoticeBar.Content = panel;
        }
        else
        {
            NoticeBar.Content = null;
        }
        NoticeBar.IsOpen = true;
    }

    /// <summary>显示一条几秒后自动消失的提示。</summary>
    private static void ShowTransientNotice(DocumentViewModel doc, DocumentNotice notice, int seconds = 4)
    {
        doc.Notice = notice;
        DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(doc.Notice, notice)) doc.Notice = null;
        }, TimeSpan.FromSeconds(seconds));
    }

    // ───────────────────────── 外观 ─────────────────────────

    private const double CaptionButtonWidth = 46;

    /// <summary>在标题栏按钮下方显示提示（按钮从右到左依次为 关闭、最大化、最小化）。</summary>
    internal void ShowCaptionToolTip(WindowDecorationsElementRole role, string text)
    {
        int indexFromRight = role switch
        {
            WindowDecorationsElementRole.CloseButton => 0,
            WindowDecorationsElementRole.MaximizeButton => 1,
            _ => 2,
        };
        CaptionToolTipText.Text = text;
        CaptionToolTip.IsVisible = true;
        CaptionToolTip.Measure(Size.Infinity);
        double width = CaptionToolTip.DesiredSize.Width;
        double buttonCenter = Bounds.Width - CaptionButtonWidth * (indexFromRight + 0.5);
        double left = Math.Clamp(buttonCenter - width / 2, 4, Math.Max(4, Bounds.Width - width - 4));
        Canvas.SetLeft(CaptionToolTip, left);
        Canvas.SetTop(CaptionToolTip, ExtendClientAreaTitleBarHeightHint + 6);
    }

    internal void HideCaptionToolTip() => CaptionToolTip.IsVisible = false;

    /// <summary>Windows 11 上使用 Mica 背景；不支持时使用纯色背景。</summary>
    private void UpdateBackdrop()
    {
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica ||
            ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            this[!BackgroundProperty] = this.GetResourceObservable("SolidBackgroundFillColorBaseBrush").ToBinding();
        }
    }

    private void UpdateThemeMenu()
    {
        ThemeSystemItem.IsChecked = Settings.Theme is not ("Light" or "Dark");
        ThemeLightItem.IsChecked = Settings.Theme == "Light";
        ThemeDarkItem.IsChecked = Settings.Theme == "Dark";
    }

    private void RestoreWindowPlacement()
    {
        var p = Settings.Window;
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) return;

        var pos = new PixelPoint(p.X, p.Y);
        // 只有窗口左上角落在某个屏幕内时才恢复位置（防止显示器拔掉后窗口跑到屏幕外）
        if (Screens.All.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 20))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = pos;
        }
        Width = p.Width;
        Height = p.Height;
        if (p.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var p = Settings.Window ?? new WindowPlacement();
        p.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            p.X = Position.X;
            p.Y = Position.Y;
            p.Width = Width;
            p.Height = Height;
        }
        Settings.Window = p;
    }

    private void SaveSession()
    {
        var tabs = AllTabs();
        var saved = tabs.Where(t => t.Doc.FilePath is not null).ToList();
        if (_active is not null) _active.Doc.CaretOffset = _active.Editor.CaretOffset;
        var session = new SessionState
        {
            Files = saved.Select(t => new SessionFile { Path = t.Doc.FilePath!, CaretOffset = t.Doc.CaretOffset }).ToList(),
            SelectedIndex = _active is null ? 0 : Math.Max(0, saved.IndexOf(_active)),
        };
        try { JsonStore.SaveSession(session); } catch (Exception) { }
    }

    // ───────────────────────── 关闭窗口 ─────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && AllTabs().Any(t => t.Doc.IsDirty))
        {
            e.Cancel = true;
            if (!Dialogs.IsOpen) _ = ConfirmCloseAsync();
            return;
        }

        SaveSession();
        SaveWindowPlacement();
        App.SaveSettings();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        foreach (var tab in AllTabs().Where(t => t.Doc.IsDirty).ToList())
        {
            if (await AskToSaveBeforeClosingAsync(tab) == SaveChoice.Cancel) return;
        }
        _closeConfirmed = true;
        Close();
    }
}
