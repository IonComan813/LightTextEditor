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
    internal MainViewModel ViewModel => _vm;
    private bool _closeConfirmed;

    private static AppSettings Settings => App.Settings;

    /// <summary>后台时的资源节省（测试用）。</summary>
    internal BackgroundMode BackgroundMode { get; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // 不在前台时少占资源（见 Services/BackgroundMode.cs）
        BackgroundMode = new BackgroundMode(this);
        Closed += (_, _) => BackgroundMode.Dispose();

        _vm.WordWrap = Settings.WordWrap;
        _vm.ShowLineNumbers = Settings.ShowLineNumbers;
        _vm.CenteredLayout = Settings.CenteredLayout;
        _vm.ShowStatusBar = Settings.ShowStatusBar;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 标题栏拖动区域（Avalonia 12：标记为 TitleBar 的元素可以拖动窗口、双击最大化）
        WindowDecorationProperties.SetElementRole(TitleBarDragArea, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(TabStripFooterDragArea, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(TabStripLeftInset, WindowDecorationsElementRole.TitleBar);
        // 给标题栏按钮（最小化/最大化/关闭，3×46）留出位置，再多留一点拖动窗口的空间。
        // 用 TabView 的右边距而不是标签栏右侧的占位元素：FluentAvalonia 按占位元素“测量出的宽度”预留空间，
        // 标签很多时那一列分不到宽度、测出来是 0，标签栏就伸到了最小化按钮下面。
        // 右边距下面是 TitleBarDragArea，照样可以拖动窗口。
        Tabs.Margin = new Thickness(0, 0, OperatingSystem.IsMacOS() ? 40 : 150, 0);
        if (OperatingSystem.IsMacOS())
        {
            // macOS 的红绿灯按钮在左侧
            TabStripLeftInset.Padding = new Thickness(76, 0, 8, 0);
        }

        // Windows：标签所在的标题栏区域按客户区处理，右键不弹系统窗口菜单（见 Services/TitleBarTabsHook.cs）
        TitleBarTabsHook.Install(this, p => TabFromSource(this.InputHitTest(p)) is not null);

        // 标签栏模板里的部件出现后，只在相关尺寸变化时更新滚动箭头（不在每次布局后遍历可视树）
        Tabs.LayoutUpdated += OnTabsLayoutUpdatedUntilReady;
        Tabs.TemplateApplied += (_, _) =>
        {
            _tabStripParts = null;
            Tabs.LayoutUpdated -= OnTabsLayoutUpdatedUntilReady;
            Tabs.LayoutUpdated += OnTabsLayoutUpdatedUntilReady;
        };
        Tabs.TabItemsSource = _tabs;
        Tabs.TabItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DocumentTab>(
            (tab, _) => tab is null ? null! : new TabHeaderView(tab, CloseTabFromButton));
        // 标签上的鼠标操作：中键关闭、双击重命名、右键菜单
        Tabs.AddHandler(PointerReleasedEvent, OnTabMiddleClick, RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(DoubleTappedEvent, OnTabDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        // 右键菜单：在右键松开时直接打开（隧道阶段，先于标签控件；只依赖 ContextRequested 时，
        // 标签控件处理掉松开事件后菜单就不出来）。ContextRequested 留给键盘（菜单键 / Shift+F10）。
        Tabs.AddHandler(PointerReleasedEvent, OnTabRightClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tabs.AddHandler(ContextRequestedEvent, OnTabContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.SelectionChanged += (_, _) => OnSelectedTabChanged();
        Tabs.AddTabButtonClick += (_, _) => NewDocument();
        NoticeBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == FAInfoBar.IsOpenProperty) NoticeHost.IsVisible = NoticeBar.IsOpen;
        };
        NoticeBar.Closed += (_, args) =>
        {
            if (args.Reason == FAInfoBarCloseReason.CloseButton) _active?.Doc.DismissNotice();
        };
        InitializeNoticeDrag();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        EditorArea.AddHandler(PointerWheelChangedEvent, OnEditorAreaWheel, RoutingStrategies.Bubble);
        EditorHost.SizeChanged += (_, e) =>
        {
            if (_active is null || !e.WidthChanged) return;
            ApplyPageLayout(_active);
        };
        AddHandler(PointerMovedEvent, OnMenuPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        Activated += (_, _) => CheckExternalChanges();
        ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var t in _tabs)
            {
                t.Highlighting?.ApplyTheme();
                ApplySelectionColors(t.Editor);
            }
        };
        Opened += (_, _) =>
        {
            UpdateBackdrop();
            RefreshTabStripLayout();
        };
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

        // 第一次启动：在笔记库里放一份 Markdown 示例和 HTML 示例，并打开 Markdown 示例
        if (!Settings.SampleNoteCreated)
        {
            try
            {
                var sample = SampleNote.Ensure(LibraryPath, Settings);
                SampleNote.EnsureHtml(LibraryPath, Settings);
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
        {
            // 打开文件可能要弹对话框（文件不存在、太大等），窗口显示出来之后才能弹
            if (IsVisible) _ = OpenStartupFilesAsync(files);
            else
            {
                void OnOpened(object? sender, EventArgs e)
                {
                    Opened -= OnOpened;
                    _ = OpenStartupFilesAsync(files);
                }
                Opened += OnOpened;
            }
        }
        else if (AllTabs().Count == 0)
            NewDocument();
    }

    /// <summary>打开命令行传入的文件；一个都没打开（如文件不存在）时留一个空白标签页。</summary>
    private async Task OpenStartupFilesAsync(string[] files)
    {
        await OpenFilesAsync(files);
        if (AllTabs().Count == 0) NewDocument();
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

    private void OnTabRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || TabFromSource(e.Source) is not { } tab) return;
        e.Handled = true;
        OpenTabContextMenu(tab);
    }

    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TabFromSource(e.Source) is not { } tab) return;
        e.Handled = true;
        OpenTabContextMenu(tab);
    }

    /// <summary>
    /// 打开标签的右键菜单。用 MenuFlyout 而不是 ContextMenu：FluentAvalonia 没有 ContextMenu 的控件模板，
    /// ContextMenu 弹出来是一个空白窗口（看起来就是“打不开”）。
    /// </summary>
    private void OpenTabContextMenu(DocumentTab tab)
    {
        if (_tabMenuPending || TabContextMenu is { IsOpen: true } && _tabMenuTab == tab) return; // 同一次右键只开一次
        TabContextMenu?.Hide();
        if (tab.Header is null) return;
        var menu = TabContextMenu = CreateTabContextMenu(tab);
        _tabMenuTab = tab;
        var target = tab.TabItem;
        _tabMenuPending = true;
        // 等这次鼠标事件处理完再弹出
        Dispatcher.UIThread.Post(() =>
        {
            _tabMenuPending = false;
            if (target.IsAttachedToVisualTree()) menu.ShowAt(target, showAtPointer: true);
        });
    }

    private bool _tabMenuPending;
    private DocumentTab? _tabMenuTab;

    /// <summary>最近打开的标签右键菜单（测试用）。</summary>
    internal MenuFlyout? TabContextMenu { get; private set; }

    private DocumentTab AddTab(DocumentViewModel doc, bool select = true)
    {
        var editor = CreateEditor(doc);
        var tab = new DocumentTab(doc, editor);
        tab.Highlighting = new EditorHighlighter(editor, doc.Extension);
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.Notice) && tab == _active) UpdateNoticeBar();
            if (e.PropertyName == nameof(DocumentViewModel.Extension))
            {
                tab.Highlighting?.SetLanguage(doc.Extension);
                if (tab.IsPreviewing && !IsMarkdown(doc)) SetPreview(tab, false);
                if (tab == _active) ApplyPageLayout(tab); // 改成 / 不再是 Markdown：版面宽度跟着变
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

    internal void Select(DocumentTab tab)
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
            tab.Preview.Zoom = _vm.Zoom;
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

    /// <summary>
    /// 让 FluentAvalonia 重新计算标签栏的宽度和左右滚动按钮。
    /// 它只在标签栏宽度变化、增删标签时计算，而且是在子元素测量之前算的：
    /// 启动时一次恢复很多标签，用到的是还没量好的尺寸，结果标签栏伸到了最小化按钮下面、
    /// 最左边的标签看不到、也没有滚动按钮。窗口显示后（尺寸都已确定）再算一次。
    /// </summary>
    private void RefreshTabStripLayout()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var mode = Tabs.TabWidthMode;
            // 改变 TabWidthMode 会触发重新计算；改回原值再算一次
            Tabs.TabWidthMode = mode == FATabViewWidthMode.Equal ? FATabViewWidthMode.SizeToContent : FATabViewWidthMode.Equal;
            Tabs.TabWidthMode = mode;
            if (_active?.Header is not null) _active.TabItem.BringIntoView();
            // 标签放不下时两端的滚动按钮：实心三角形（自带的是很小的字体图标，模板里设的值样式改不了）
            foreach (var button in Tabs.GetVisualDescendants().OfType<RepeatButton>())
            {
                bool left = button.Name == "ScrollDecreaseButton";
                if (!left && button.Name != "ScrollIncreaseButton") continue;
                var arrow = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(left ? "M7,0 L7,10 L0,5 Z" : "M0,0 L7,5 L0,10 Z"),
                    Width = 7,
                    Height = 10,
                    Stretch = Stretch.Fill,
                };
                // 颜色跟随按钮模板里的 ContentPresenter（悬停、按下、不可用时它的前景色会变）
                arrow.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Avalonia.Data.Binding("Foreground")
                {
                    RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor)
                    {
                        AncestorType = typeof(Avalonia.Controls.Presenters.ContentPresenter),
                    },
                });
                button.Content = arrow;
                ToolTip.SetTip(button, left ? "向左滚动标签" : "向右滚动标签");
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 标签放得下时不显示两端的滚动箭头，放不下时显示。
    /// FluentAvalonia 只在某些时机（而且是在子元素测量之前）判断，窗口变宽（如最大化）后箭头常常留着；
    /// 这里在标签栏、标签列表内容、左侧图标或“+”按钮的尺寸变化后按实际尺寸再判断一次。
    /// </summary>
    private void UpdateTabScrollArrows()
    {
        if (_tabStripParts is not var (list, scroller, left, add)) return;

        // 标签可用的宽度 = 标签栏宽度 − 左侧图标 − “+” 按钮
        double space = Tabs.Bounds.Width - (left?.Bounds.Width ?? 0) - (add?.Bounds.Width ?? 0);
        if (space <= 0) return;
        bool overflow = scroller.Extent.Width > space + 0.5;
        var wanted = overflow ? Avalonia.Controls.Primitives.ScrollBarVisibility.Visible : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        if (ScrollViewer.GetHorizontalScrollBarVisibility(list) != wanted)
            ScrollViewer.SetHorizontalScrollBarVisibility(list, wanted);
        if (Math.Abs(list.MaxWidth - space) > 0.5) list.MaxWidth = space;
        // 放得下时回到最左边（之前滚动过的话，最左边的标签会一直被挡住一部分）
        if (!overflow && scroller.Offset.X != 0) scroller.Offset = scroller.Offset.WithX(0);
    }

    /// <summary>标签栏模板里用到的部件（找一次后缓存）。</summary>
    private (Control List, ScrollViewer Scroller, Control? Left, Control? Add)? _tabStripParts;

    private void OnTabsLayoutUpdatedUntilReady(object? sender, EventArgs e)
    {
        var list = Tabs.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.Primitives.FATabViewListView>().FirstOrDefault();
        if (list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller) return;
        var left = Tabs.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "LeftContentPresenter");
        var add = Tabs.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "AddButtonHost");

        Tabs.LayoutUpdated -= OnTabsLayoutUpdatedUntilReady;
        _tabStripParts = (list, scroller, left, add);
        // 标签增减、标题变化 → 列表内容宽度（Extent）变化；窗口变宽变窄 → 标签栏宽度变化
        scroller.ScrollChanged += (_, _) => UpdateTabScrollArrows();
        Tabs.SizeChanged += (_, _) => UpdateTabScrollArrows();
        if (left is not null) left.SizeChanged += (_, _) => UpdateTabScrollArrows();
        if (add is not null) add.SizeChanged += (_, _) => UpdateTabScrollArrows();
        UpdateTabScrollArrows();
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
    // 拖动过程中只移动标签的显示位置（渲染位移），不改标签的顺序——被拖动的标签跟着鼠标走、浮在其他标签上方，
    // 它经过的标签滑到让出的位置；松开时才真正调整一次顺序，然后所有标签从当前显示的位置滑到新位置。
    // （拖动中反复调整顺序时，标签栏会回收、重建标签控件，容易出现标签错位或消失。）

    private const double TabDragThreshold = 6;
    private const double TabSlideMilliseconds = 160;
    private DocumentTab? _dragTab;
    private Point _dragStart;
    private bool _isDraggingTab;
    private double _dragGrabOffset;   // 鼠标相对被拖动标签左边缘的位置
    private int _dragTargetIndex;     // 松开时被拖动标签要去的位置
    private List<(DocumentTab Tab, double Left, double Width)> _dragLayout = []; // 开始拖动时各标签的布局位置
    private readonly Dictionary<Control, TabSlide> _tabSlides = [];
    private DispatcherTimer? _tabSlideTimer;

    private sealed record TabSlide(double From, double To, long Start);

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
            if (!BeginTabDrag(e.Pointer)) { EndTabDrag(); return; }
        }
        e.Handled = true;

        int from = _dragLayout.FindIndex(t => t.Tab == _dragTab);
        if (from < 0 || _dragTab.Header is null) { EndTabDrag(); return; }
        var (_, draggedLeft, width) = _dragLayout[from];

        // 被拖动的标签显示在鼠标下（限制在标签栏范围内）
        double minLeft = _dragLayout[0].Left;
        double maxLeft = _dragLayout[^1].Left + _dragLayout[^1].Width - width;
        double left = Math.Clamp(pos.X - _dragGrabOffset, minLeft, Math.Max(minLeft, maxLeft));

        // 目标位置：向右拖时，被拖动标签的右边缘越过右侧标签的中线，那个标签就让位；向左同理看左边缘。
        // （按中心算的话，被拖动的标签——选中、加粗、显示 ×，比别的标签宽——要几乎完全盖住邻居才交换，
        // 看起来像邻居消失了。）用未限制的位置，才能拖到最前 / 最后。
        double rawLeft = pos.X - _dragGrabOffset;
        int to = from;
        for (int i = 0; i < _dragLayout.Count; i++)
        {
            double mid = _dragLayout[i].Left + _dragLayout[i].Width / 2;
            if (i > from && rawLeft + width > mid) to++;
            else if (i < from && rawLeft < mid) to--;
        }
        _dragTargetIndex = to;

        // 经过的标签让位：向左或向右移动被拖动标签的宽度
        for (int i = 0; i < _dragLayout.Count; i++)
        {
            if (i == from || _dragLayout[i].Tab.Header is null) continue;
            double shift = from < to && i > from && i <= to ? -width
                         : to < from && i >= to && i < from ? width
                         : 0;
            var item = _dragLayout[i].Tab.TabItem;
            if (_tabSlides.TryGetValue(item, out var slide) ? slide.To != shift : TabOffset(item) != shift)
                SlideTab(item, TabOffset(item), shift);
        }

        var dragged = _dragTab.TabItem;
        _tabSlides.Remove(dragged);
        dragged.ZIndex = 100; // 浮在经过的标签上方
        SetTabOffset(dragged, left - draggedLeft);
    }

    /// <summary>开始拖动：选中被拖动的标签，记下各标签的布局位置（拖动过程中顺序不变）。</summary>
    private bool BeginTabDrag(IPointer pointer)
    {
        if (_dragTab?.Header is null) return false;
        _isDraggingTab = true;
        pointer.Capture(Tabs);
        Select(_dragTab); // 拖哪个标签就选中哪个（选中后标题加粗、宽度会变，所以之后再量位置）
        Tabs.UpdateLayout();
        _dragLayout = _tabs.Where(t => t.Header is not null)
            .Select(t => (t, TabLayoutLeft(t.TabItem), t.TabItem.Bounds.Width))
            .OrderBy(t => t.Item2)
            .ToList();
        if (_dragTab.Header is null || _dragLayout.Count == 0) return false;
        _dragGrabOffset = _dragStart.X - TabLayoutLeft(_dragTab.TabItem);
        _dragTargetIndex = _dragLayout.FindIndex(t => t.Tab == _dragTab);
        return true;
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

    /// <summary>
    /// 结束拖动：把标签放到目标位置，然后所有标签从现在显示的位置滑到新的布局位置。
    /// </summary>
    private void EndTabDrag()
    {
        var dragTab = _dragTab;
        bool wasDragging = _isDraggingTab;
        _dragTab = null;
        _isDraggingTab = false;
        if (!wasDragging || dragTab is null) return;

        // 各标签现在显示在哪里
        var shown = new Dictionary<DocumentTab, double>();
        foreach (var t in _tabs)
            if (t.Header is not null) shown[t] = TabLayoutLeft(t.TabItem) + TabOffset(t.TabItem);

        int from = _tabs.IndexOf(dragTab);
        if (from >= 0) MoveTab(from, Math.Clamp(_dragTargetIndex, 0, _tabs.Count - 1));
        _dragLayout = [];
        _tabSlides.Clear();
        ResetTabContainers();
        Tabs.UpdateLayout();

        foreach (var t in _tabs)
        {
            if (t.Header is null) continue;
            var item = t.TabItem;
            if (t == dragTab) item.ZIndex = 100; // 滑到位之前仍在最上面
            SlideTab(item, shown.TryGetValue(t, out var oldLeft) ? oldLeft - TabLayoutLeft(item) : 0, 0);
        }
    }

    /// <summary>清掉所有标签控件（包括标签栏回收备用的）上的位移和层级。</summary>
    private void ResetTabContainers()
    {
        foreach (var item in Tabs.GetVisualDescendants().OfType<FATabViewItem>())
        {
            if (item.RenderTransform is TranslateTransform { X: not 0 }) SetTabOffset(item, 0);
            RestoreTabZIndex(item);
        }
    }

    /// <summary>标签在布局中的左边缘（不含拖动 / 动画产生的位移），相对于标签栏。</summary>
    private double TabLayoutLeft(Control item) =>
        (item.TranslatePoint(default, Tabs)?.X ?? 0) - TabOffset(item);

    private static double TabOffset(Control item) => (item.RenderTransform as TranslateTransform)?.X ?? 0;

    private static void SetTabOffset(Control item, double x)
    {
        if (item.RenderTransform is TranslateTransform t) t.X = x;
        else item.RenderTransform = new TranslateTransform(x, 0);
    }

    /// <summary>恢复标签的层级：选中的标签为 20（FATabViewItem 自己的设定，盖住相邻标签的分隔线），其他为 0。</summary>
    private static void RestoreTabZIndex(Control item) =>
        item.ZIndex = item is FATabViewItem { IsSelected: true } ? 20 : 0;

    /// <summary>让标签的位移从 <paramref name="from"/> 平滑地变到 <paramref name="to"/>。</summary>
    private void SlideTab(Control item, double from, double to)
    {
        if (Math.Abs(from - to) < 0.5)
        {
            _tabSlides.Remove(item);
            FinishSlide(item, to);
            return;
        }
        SetTabOffset(item, from);
        _tabSlides[item] = new TabSlide(from, to, System.Diagnostics.Stopwatch.GetTimestamp());
        _tabSlideTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, OnTabSlideTick);
        _tabSlideTimer.Start();
    }

    private void FinishSlide(Control item, double offset)
    {
        SetTabOffset(item, offset);
        // 拖动已经结束：恢复层级
        if (!_isDraggingTab && offset == 0) RestoreTabZIndex(item);
    }

    private void OnTabSlideTick(object? sender, EventArgs e)
    {
        foreach (var (item, slide) in _tabSlides.ToList())
        {
            double t = System.Diagnostics.Stopwatch.GetElapsedTime(slide.Start).TotalMilliseconds / TabSlideMilliseconds;
            if (t >= 1)
            {
                _tabSlides.Remove(item);
                FinishSlide(item, slide.To);
            }
            else
            {
                double eased = 1 - Math.Pow(1 - t, 3); // 先快后慢
                SetTabOffset(item, slide.From + (slide.To - slide.From) * eased);
            }
        }
        if (_tabSlides.Count == 0)
        {
            _tabSlideTimer?.Stop();
            if (!_isDraggingTab) ResetTabContainers(); // 保险：动画全部结束后，不留任何位移
        }
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

    private MenuFlyout CreateTabContextMenu(DocumentTab tab)
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
        var menu = new MenuFlyout
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

    // ───────────────────────── 菜单 ─────────────────────────

    /// <summary>
    /// 鼠标移到菜单里不可用的项上时，取消其他项的高亮并收起它们的子菜单。
    /// （不可用的项收不到鼠标事件，Avalonia 不会切换高亮：从“更改后缀”移到灰色的“移动到笔记库”时，
    /// “更改后缀”会一直高亮。）
    /// </summary>
    private void OnMenuPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!MainMenu.IsOpen) return;
        DeselectAroundDisabledItem(MainMenu.Items.OfType<MenuItem>(), e);
    }

    private static void DeselectAroundDisabledItem(IEnumerable<MenuItem> items, PointerEventArgs e)
    {
        foreach (var parent in items.Where(i => i.IsSubMenuOpen).ToList())
        {
            var children = parent.Items.OfType<MenuItem>().ToList();
            var disabled = children.FirstOrDefault(c => !c.IsEffectivelyEnabled && c.IsEffectivelyVisible &&
                new Rect(c.Bounds.Size).Contains(e.GetPosition(c)));
            if (disabled is not null)
            {
                foreach (var sibling in children.Where(c => c != disabled && (c.IsSelected || c.IsSubMenuOpen)))
                {
                    sibling.IsSubMenuOpen = false;
                    sibling.IsSelected = false;
                }
                return;
            }
            DeselectAroundDisabledItem(children, e);
        }
    }

    // ───────────────────────── 编辑器 ─────────────────────────

    private TextEditor CreateEditor(DocumentViewModel doc)
    {
        var editor = new TextEditor
        {
            Document = doc.Document,
            FontFamily = Fonts.InstalledFamilies(Settings.EditorFontFamily),
            FontSize = Settings.EditorFontSize * _vm.Zoom,
            ShowLineNumbers = _vm.ShowLineNumbers,
            WordWrap = _vm.WordWrap,
            Background = Brushes.Transparent,
            // 不要设置 HorizontalScrollBarVisibility：AvaloniaEdit 用它实现自动换行（换行 = 禁用横向滚动），
            // 在 WordWrap 之后再设为 Auto 会让新打开的标签不换行（要关掉再打开才生效）
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
        if (_active?.Editor == editor) ApplyPageLayout(_active);
    }

    // ───────────────────────── 居中布局 ─────────────────────────

    /// <summary>
    /// 版面宽度（编辑器和 Markdown 预览）：
    /// <list type="bullet">
    /// <item>居中布局：窗口中间固定宽度的“页面”（宽度随缩放变化），两侧是颜色稍深的底色，类似 Word。
    ///   不自动换行时，超出页面的长行在页面内横向滚动（滚动条在页面底部）。</item>
    /// <item>否则：代码和纯文本铺满窗口；Markdown 靠左、宽度不超过页面宽度（预览中的代码块、提示框不会拉到最右边），
    ///   滚动条仍在窗口右边。</item>
    /// </list>
    /// </summary>
    private void ApplyPageLayout(DocumentTab tab)
    {
        bool centered = _vm.CenteredLayout;
        double pageWidth = Settings.CenteredLayoutWidth * _vm.Zoom;
        double available = EditorHost.Bounds.Width;
        double width = available > 0 ? Math.Min(pageWidth, available) : pageWidth; // 窗口比页面窄时铺满

        foreach (var view in new Control?[] { tab.Editor, tab.Preview })
        {
            if (view is not Avalonia.Controls.Primitives.TemplatedControl page) continue;
            page.Width = centered ? width : double.NaN;
            page.HorizontalAlignment = centered ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;
            if (centered)
            {
                page[!BackgroundProperty] = this.GetResourceObservable("PageBackgroundBrush").ToBinding();
                page[!BorderBrushProperty] = this.GetResourceObservable("PageBorderBrush").ToBinding();
                page.BorderThickness = new Thickness(1, 0);
            }
            else
            {
                page.Background = Brushes.Transparent;
                page.BorderThickness = default;
            }
        }

        // Markdown 源码：自动换行时右侧留白，换行宽度 = 页面宽度（编辑器本身仍铺满，滚动条在窗口右边）。
        // 不自动换行时不留白：留白处不显示文字，长行会被截在页面宽度处、右边一片空白。
        bool limitMarkdown = !centered && IsMarkdown(tab.Doc);
        bool limitSource = limitMarkdown && _vm.WordWrap && available > pageWidth;
        tab.Editor.Padding = limitSource ? new Thickness(0, 0, available - pageWidth, 0) : default;
        // Markdown 预览：内容最宽为页面宽度（缩放由预览自己按比例放大，这里用 100% 时的宽度）
        tab.Preview?.SetContentMaxWidth(limitMarkdown ? Settings.CenteredLayoutWidth : double.PositiveInfinity);

        if (centered) EditorArea[!Border.BackgroundProperty] = this.GetResourceObservable("PageDeskBrush").ToBinding();
        else EditorArea.Background = null;
    }

    /// <summary>居中布局时，在页面两侧的空白处滚动鼠标滚轮也能滚动页面。</summary>
    private void OnEditorAreaWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || _active is null || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        ScrollViewer? scroller = _active.IsPreviewing
            ? _active.Preview
            : _active.Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(); // 编辑器模板里的滚动区域
        if (scroller is null) return;
        const double step = 50;
        double range = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(scroller.Offset.Y - e.Delta.Y * step, 0, range));
        e.Handled = true;
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

    private static double ClampZoom(double zoom) => Math.Round(Math.Clamp(zoom, 0.5, 4.0), 1);

    private void SetZoom(double zoom)
    {
        _vm.Zoom = ClampZoom(zoom);
        if (_active is null) return;
        ApplyViewOptions(_active.Editor);
        if (_active.Preview is not null) _active.Preview.Zoom = _vm.Zoom;
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
            case nameof(MainViewModel.CenteredLayout):
                if (_active is not null) ApplyViewOptions(_active.Editor);
                Settings.CenteredLayout = _vm.CenteredLayout;
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
                if (action.Children is { } children)
                {
                    var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                    foreach (var child in children) flyout.Items.Add(ChoiceMenuItem(child));
                    panel.Children.Add(new DropDownButton { Content = action.Label, Flyout = flyout });
                    continue;
                }
                var button = new Button
                {
                    Content = action.Icon == NoticeIcon.Checkmark
                        ? new FASymbolIcon { Symbol = FASymbol.Checkmark, FontSize = 16 }
                        : action.Label,
                };
                if (action.Icon != NoticeIcon.None)
                {
                    ToolTip.SetTip(button, action.Label);
                    Avalonia.Automation.AutomationProperties.SetName(button, action.Label);
                }
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

    /// <summary>单选菜单项（当前项打勾）；<see cref="NoticeAction.IsDimmed"/> 时文字为灰色但仍可选择。</summary>
    private static MenuItem ChoiceMenuItem(NoticeAction choice)
    {
        var item = new MenuItem
        {
            Header = choice.Label,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = choice.IsChecked,
        };
        if (choice.IsDimmed) item.Classes.Add("dimmed");
        item.Click += (_, _) => choice.Execute();
        return item;
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
