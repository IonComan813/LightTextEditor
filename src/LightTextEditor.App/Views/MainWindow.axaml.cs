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
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;
using Avalonia.Controls.Chrome;

namespace LightTextEditor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<FATabViewItem> _tabItems = [];
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
            TabStripLeftInset.Width = 76;
            TabStripFooterDragArea.MinWidth = 40;
        }

        Tabs.TabItemsSource = _tabItems;
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

    internal List<DocumentTab> AllTabs() =>
        _tabItems.Select(t => (DocumentTab)t.Tag!).ToList();

    private DocumentTab AddTab(DocumentViewModel doc, bool select = true)
    {
        var editor = CreateEditor(doc);
        var tab = new DocumentTab(doc, editor);

        tab.CloseButton.Click += async (_, e) =>
        {
            e.Handled = true;
            await CloseTabAsync(tab);
        };
        tab.TabItem.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                await CloseTabAsync(tab);
            }
        };
        tab.TabItem.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await RenameAsync(tab);
        };
        tab.TabItem.ContextMenu = CreateTabContextMenu(tab);
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.Notice) && tab == _active) UpdateNoticeBar();
        };

        EditorHost.Children.Add(editor);
        _tabItems.Add(tab.TabItem);
        if (select) Select(tab);
        return tab;
    }

    private void RemoveTab(DocumentTab tab)
    {
        var tabs = AllTabs();
        int index = tabs.IndexOf(tab);
        _tabItems.Remove(tab.TabItem);
        EditorHost.Children.Remove(tab.Editor);

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
        if (!ReferenceEquals(Tabs.SelectedItem, tab.TabItem))
            Tabs.SelectedItem = tab.TabItem;
        OnSelectedTabChanged();
    }

    private void OnSelectedTabChanged()
    {
        var tab = (Tabs.SelectedItem as FATabViewItem)?.Tag as DocumentTab;
        if (tab is null || tab == _active) return;

        if (_active is not null)
        {
            _active.Doc.CaretOffset = _active.Editor.CaretOffset;
            _active.Editor.IsVisible = false;
        }

        _active = tab;
        tab.Editor.IsVisible = true;
        _vm.ActiveDocument = tab.Doc;
        UpdateCaretStatus();
        UpdateNoticeBar();
        FocusEditor();
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
                new Separator(),
                copyPath,
                reveal,
            },
        };
        menu.Opening += (_, _) =>
        {
            copyPath.IsEnabled = reveal.IsEnabled = tab.Doc.FilePath is not null;
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

    /// <summary>行号与正文之间不用分隔虚线，改为留白，更接近 Windows 11 记事本的观感。</summary>
    private static void StyleLineNumberMargin(TextEditor editor)
    {
        foreach (var margin in editor.TextArea.LeftMargins)
        {
            if (margin is AvaloniaEdit.Editing.LineNumberMargin numbers)
                numbers.Margin = new Thickness(12, 0, 4, 0);
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
        foreach (var t in AllTabs()) t.Editor.FontSize = Settings.EditorFontSize * _vm.Zoom;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.WordWrap):
                foreach (var t in AllTabs()) t.Editor.WordWrap = _vm.WordWrap;
                Settings.WordWrap = _vm.WordWrap;
                App.SaveSettings();
                break;
            case nameof(MainViewModel.ShowLineNumbers):
                foreach (var t in AllTabs())
                {
                    t.Editor.ShowLineNumbers = _vm.ShowLineNumbers;
                    StyleLineNumberMargin(t.Editor);
                }
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
            Select(tab);
            var choice = await Dialogs.ConfirmSaveAsync(this, tab.Doc.Title);
            if (choice == SaveChoice.Cancel) return;
            if (choice == SaveChoice.Save && !await SaveAsync(tab)) return;
        }
        _closeConfirmed = true;
        Close();
    }
}
