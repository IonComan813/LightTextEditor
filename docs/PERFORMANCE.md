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
| “未保存”标记先比较文本长度，长度和保存时相同才计算哈希 | `DocumentViewModel.IsTextChanged`、`TextFingerprint` | 打字时长度几乎总是不同，不用读文本。哈希用 XxHash3，分块读取文档、不复制整篇文本：1100 万字符（约 22 MB）约 16 ms |
| 超长的行只排版光标附近的一段 | `Services/LongLineWindow.cs` | AvaloniaEdit 每次重建一行都把整行交给排版引擎，一行几百万字符（压缩的 JSON）时打开、每次按键都要几秒，界面“未响应”。超过 1 万字符的行按“窗口”显示：光标前后各约 2.5 屏，其余折叠成一句提示；光标离窗口边缘不足 1.25 屏时重新取窗口并保持光标在屏幕上的位置。文档内容不变 |
| 有超长行的文档不做语法高亮 | `EditorHighlighter`（`IsSuspendedForLongLines`） | TextMate 每次修改都重新分析长行的前 1 万个字符（部分在界面线程上），着色时每个记号又遍历整行的显示元素，按一次键几百毫秒；长行都去掉后自动恢复 |
| 输入法“周围文字”最多取光标前后各 1000 个字符 | `Services/EditorPreedit.cs` | 原来每次移动光标都把整行复制成字符串交给输入法，长行时每次几 MB |
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
`MarkdownPreview`、`ReleaseMemoryAfterClosingTabs`、`LargeFile`。无头环境是软件渲染，绝对数值与 Windows 不同，只用于前后对比。

`LargeFile`：打开一个大 JSON，测打开并显示、每次按键、每次翻页的耗时（主线程）。参数：
`PERF_SHAPE`（`minified` 整个文件一行 / `pretty` 格式化成多行）、`PERF_RECORDS`（对象个数，默认 21000；
单行 3.65 MB 约 27700）、`PERF_EXT`（默认 `.json`，`.txt` 不做语法高亮）、`PERF_WRAP=0`（不自动换行）、
`PERF_ASCII=1`（不含中文）、`PERF_CARET`（输入的位置）、`PERF_SETTLE_MS`（先等后台语法分析做完）。

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

### 大文件（2026-11-24，同样的环境）

压缩成一行的 JSON（约 3.4~3.7 MB 只有一行，含中文），默认设置（自动换行、JSON 高亮）：

| 场景 | 指标 | 修改前 | 修改后 |
|------|------|--------|--------|
| 实际程序（发布版）打开 3.37 MB 单行 JSON | 启动到首帧 | 65.6 s | 0.76 s |
| | 工作集 / 私有内存 | 1489 MB / 1624 MB | 241 MB / 295 MB |
| | 托管堆 | 806 MB | 22 MB |
| 无头基准 `LargeFile`（3.67 MB，`PERF_RECORDS=27700`） | 打开并显示 | 59.4 s | 0.22 s |
| | 每次按键 | 62.4 s | 77 ms（在行中间输入 93 ms） |
| | 不自动换行时打开并显示 | 14.5 s | 0.18 s |

无头基准里小文件每次按键本身也要 25~40 ms（软件渲染整个窗口）。格式化成多行的同样大小的 JSON 原来就不卡（每次按键约 60 ms，后台语法分析约 4 s CPU），这次没有变化。

Windows 专门的优化（效率模式、内存优先级、收缩工作集）无法在这里测量，可以在 Windows 上最小化窗口几秒后，
在任务管理器的“详细信息”中看内存（专用工作集）和“效率模式”列。

## 看过但没有改的

- Avalonia 的输入、排版和渲染占每次按键开销的绝大部分（本程序自己的代码约 1%）。
- FluentAvalonia 主题在启动时加载 DataGrid、ColorPicker 等程序集（主题自带的控件样式），去掉需要改主题库。
- 多个标签页共用同一份编译后的语法：TextMateSharp 的语法对象在多个后台着色线程同时使用时不安全，没有做。
- 几千字符、记号很多的一行（如 3000~10000 字符的单行 JSON）开着语法高亮时，每次按键在无头环境要 150 ms 以上（纯文本约 35 ms）：
  每个记号都是一段单独排版的文字，排版引擎逐段处理。没有为此关掉高亮（会让很多普通文件失去高亮）；超过 1 万字符的行见上面的“窗口”显示。
- `TieredPGO`、`GCConserveMemory`：测不出差别。
- 渲染方式（GPU / 软件）、Mica / 亚克力背景：保持默认；需要在 Windows 实机上比较 GPU 内存和 CPU 后再决定。
