# 性能与资源占用

记录已做的优化、测量方法和结果，方便以后对比。

## 已做的优化

### 通用（所有系统）

| 优化 | 位置 | 效果 |
|------|------|------|
| Markdown 语法高亮只加载代码块实际用到的语言 | `Services/SyntaxHighlighting.cs`（`MarkdownRegistryOptions`、`EditorHighlighter`） | TextMate 的 Markdown 语法为代码块引用了约 70 种语言，原来每个 Markdown 标签页都会全部编译（约 30 MB）。现在只编译文档里出现的语言及其依赖（约 1~3 MB），之后写了新语言的代码块时（停止输入 0.8 秒后）再补上 |
| 纯文本不安装 TextMate | 同上 | `.txt` / `.log` 没有后台着色和对应的内存 |
| Markdown 预览的样式、配色、字体、扩展在第一次预览时才加载 | `App.EnsureMarkdownResources`、`Styles/MarkdownStyles.axaml` | 启动时不加载 LiveMarkdown、Markdig、RaTeX、Mermaid 等 7 个程序集 |
| 标签栏滚动箭头只在相关尺寸变化时更新 | `MainWindow.axaml.cs`（`OnTabsLayoutUpdatedUntilReady`） | 原来每次布局（每次按键）都遍历三次标签栏的可视树 |
| 未命名文档的标题只在改动可能影响第一行时重新计算 | `DocumentViewModel.UntitledFirstLineTitle` | 原来每次按键复制开头 4096 个字符 |
| 编辑器字体列表去掉没安装的字体 | `Fonts.InstalledFamilies` | Avalonia 排版每段文字时都会逐个查找列表里的字体，没安装的每次都查找失败（如 Windows 10 没有 Cascadia Mono） |
| 后台时回收内存 | `Services/BackgroundMode.cs` | 最小化 2 秒后或在后台 1 分钟后做一次压缩式垃圾回收（`GCCollectionMode.Aggressive`），把空闲内存还给系统 |
| 不用并发（后台）垃圾回收 | `LightTextEditor.App.csproj` | 少一个线程，约少 9 MB 内存；堆很小，完整回收只需几毫秒 |
| 发布时 ReadyToRun 预编译 | 同上（`dotnet publish -r <RID>` 时自动开启） | 启动时间约减半，内存更少；安装包大 28 MB |
| 发布时不带第三方库的调试符号 | 同上 | Windows 发布目录 176 MB → 76 MB |

### Windows 专门

都在 `Services/BackgroundMode.cs`（`WindowsProcess`），失败时静默忽略：

- **效率模式（EcoQoS）**：窗口未激活或最小化时开启（`SetProcessInformation` + `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`），调度器优先使用能效核心、较低频率；最小化时在 Windows 11 上还不再为本进程保持高精度计时器（`IGNORE_TIMER_RESOLUTION`）。回到前台时交还系统决定。
- **内存优先级**：最小化时降为低（`MEMORY_PRIORITY_LOW`），内存紧张时先换出本程序的页面；恢复时改回正常。
- **收缩工作集**：最小化并回收内存后调用 `SetProcessWorkingSetSize(-1, -1)`，任务管理器里的内存占用随之下降，需要时系统会自动调回。

## 测量方法

### 1. 实际启动（推荐在 Windows 上用这个）

设置环境变量 `LTE_STARTUP_REPORT` 为一个文件路径，程序在主窗口第一次显示并绘制完成、再空闲 2 秒后，
把以下数据追加到该文件（制表符分隔）然后退出：

启动到首帧（毫秒） · 工作集（MB） · 私有内存（MB） · 托管堆（MB） · 线程数 · 已加载程序集数

```bat
dotnet publish src\LightTextEditor.App -c Release -r win-x64 --self-contained false -o publish
set LTE_STARTUP_REPORT=%TEMP%\lte-startup.tsv
publish\LightTextEditor.exe
publish\LightTextEditor.exe 笔记1.md 笔记2.md 笔记3.md
type %TEMP%\lte-startup.tsv
```

### 2. 无头基准（Linux / CI）

`tests/LightTextEditor.App.Tests/PerformanceProbe.cs`，只有设置 `LTE_PERF=1` 时运行，每个场景单独运行（全新进程）：

```bash
LTE_PERF=1 LTE_PERF_OUT=perf.tsv dotnet test tests/LightTextEditor.App.Tests -c Release --filter "FullyQualifiedName~PerformanceProbe.Typing"
```

场景：`Startup_OpenTextFile`、`Typing`（每次按键的内存分配）、`OpenFiles`（`PERF_EXT`、`PERF_COUNT`）、
`MarkdownPreview`、`ReleaseMemoryAfterClosingTabs`。无头环境是软件渲染，绝对数值与 Windows 不同，只用于前后对比。

## 结果（2026-11-19，Linux + Xvfb 软件渲染，5 次取中位数）

实际程序（发布版）：

| 场景 | 指标 | 优化前 | 优化后 |
|------|------|--------|--------|
| 启动（空白文档） | 启动到首帧 | 2078 ms | 907 ms |
| | 私有内存 | 288 MB | 270 MB |
| | 程序集 / 线程 | 74 / 26 | 64 / 25 |
| 启动并打开 3 个 .md | 启动到首帧 | 2797 ms | 1223 ms |
| | 工作集 | 399 MB | 300 MB |
| | 私有内存 | 464 MB | 359 MB |
| | 托管堆 | 97 MB | 26 MB |

无头基准：

| 场景 | 优化前 | 优化后 |
|------|--------|--------|
| 同时打开 3 个 .md：托管堆 / 耗时 | 97 MB / 1.3 s | 8 MB / 0.25 s |
| 每次按键分配的内存 | 172 KB | 139 KB |
| 关闭 6 个用过多种语言的 Markdown 标签后：GC 已提交内存 → 后台回收后 | 210 MB | 35 MB（私有内存少约 175 MB） |

Windows 专门的优化（效率模式、内存优先级、收缩工作集）无法在这里测量，可以在 Windows 上最小化窗口几秒后，
在任务管理器的“详细信息”中看内存（专用工作集）和“效率模式”列。

## 看过但没有改的

- Avalonia 的输入、排版和渲染占每次按键开销的绝大部分（本程序自己的代码约 1%）。
- FluentAvalonia 主题在启动时加载 DataGrid、ColorPicker 等程序集（主题自带的控件样式），去掉需要改主题库。
- 多个标签页共用同一份编译后的语法：TextMateSharp 的语法对象在多个后台着色线程同时使用时不安全，没有做。
- `TieredPGO`、`GCConserveMemory`：测不出差别。
- 渲染方式（GPU / 软件）、Mica / 亚克力背景：保持默认；需要在 Windows 实机上比较 GPU 内存和 CPU 后再决定。
