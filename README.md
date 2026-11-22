# LightTextEditor

基于 [Avalonia](https://avaloniaui.net/) 的轻量文本编辑器，Fluent Design 风格。

- 多标签页，标签栏融入标题栏（Windows 11 上使用 Mica 背景；自绘最小化/最大化/关闭按钮，带中文提示）
- **保存到笔记库不用选位置**：新文档第一次保存时只确认文件名和后缀（默认取首行文字或时间 + `.txt`），都可修改
- 状态栏或“文件 → 更改后缀”可以把文件直接转换为其他后缀（如 `.md`、`.py`）
- 手动保存；有未保存更改时标签上显示 ●，鼠标移上去变成 ×。按内容判断（长度 + XxHash3 哈希）：打了几个字又删掉，内容和保存时一样，就不显示 ●
- 读取时识别编码：BOM → 记住的编码 / `.editorconfig` → 无 BOM 的 UTF-16 → UTF-8 → 统计检测（GBK、Big5、Shift-JIS、EUC-KR 等）
  - 拿不准时提示“请确认当前文件的编码格式”：[更换编码重试▾]（试过的编码显示为灰色）[✓]（记住，下次直接用）；点 ✓ 后再询问是否转换为 UTF-8。也可以先关掉，之后在“编码”菜单里确认或更换
  - 提示条可以拖到别处，不挡住正文；双击提示条空白处回到左上角
  - 顶部“编码”菜单随时可以换编码重新打开、以其他编码保存、转换为 UTF-8
  - 保存时保持原编码，绝不静默转码；有无法识别的字节、或编码无法表示新输入的字符时先提示
  - 新文件遵循 `.editorconfig` 的 `charset`
- 代码语法高亮（VS Code 的 TextMate 语法和 Light+ / Dark+ 配色），按后缀自动选择语言
- Markdown：`Ctrl+E` 在源码和渲染预览之间切换；`Ctrl+V` 粘贴截图或图片文件、拖入图片，自动存到 `assets/` 并插入 `![]()`
  - 预览支持 GFM 表格 / 任务列表、提示框（`> [!NOTE]`）、代码高亮、LaTeX 公式（`$…$` / `$$…$$`）、Mermaid 图表，以及 `<u>`、`<sub>`、`<sup>`、`<mark>`、`<br>` 等常用行内 HTML
  - 预览中可以直接勾选任务列表（改写源码中的 `[ ]` / `[x]`，按 Ctrl+S 保存）；Ctrl+滚轮缩放
  - 第一次启动时笔记库里会有一篇“Markdown 示例”（演示所有格式）和一份“HTML 示例”，也可以从“帮助”菜单重新打开；网页文件可以“查看 → 在浏览器中打开”
- 笔记库以外的文件：打开或另存为到笔记库以外时，在笔记库的“外部文件”文件夹里放一个快捷方式，文件仍保存在原位置；“文件 → 移动到笔记库”可以把它搬进笔记库（Markdown 引用的本地图片一同迁移）
- 标签页可以拖动排序，右键有关闭其他、复制路径等菜单；标签放不下时两端有滚动按钮；打开文件总是放在新标签页中
- “查看 → 居中布局”：正文显示在窗口中间 A4 纸宽（794 px）的页面上，类似 Word；不开时代码和纯文本铺满窗口
  - Markdown（源码和预览）总是居中布局并自动换行，这两个菜单项此时打勾且不可更改；预览左右留出 A4 窄页边距（48 px），版面与以后的导出、打印一致
- 外部修改检测、会话恢复、单实例（从资源管理器打开的文件会进入已打开的窗口）
  - 外部修改：切回本程序时，检查当前显示的标签页的文件是否被其他程序改过（先比较修改时间，再比较内容的 XxHash3，内容没变不算）；改过时提示“载入磁盘上的版本”或“保留我的版本”，不自动重新加载。窗口在前台时不检查，保存时会再确认一次是否覆盖
- Windows 安装包：右键“打开方式”中可以选择本程序打开文本 / Markdown / 代码文件，也可以在系统“默认应用”中设为默认程序
- 资源占用低：Markdown 只加载代码块用到的语法、预览相关的组件在第一次预览时才加载；窗口在后台时回收内存，Windows 上进入效率模式（EcoQoS）

开发计划见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/LightTextEditor.App
```

```bash
dotnet test
```

发布（预编译为本机代码，启动更快；不带第三方库的调试符号）：

```bash
dotnet publish src/LightTextEditor.App -c Release -r win-x64 --self-contained false -o publish
```

性能优化和测量方法见 [docs/PERFORMANCE.md](docs/PERFORMANCE.md)。

## 发版

GitHub Actions（`.github/workflows/release.yml`）自动构建两种 Windows 安装包（基于 [Velopack](https://velopack.io/)，都是单个文件），附加到对应的 Release：

| 文件 | 说明 |
|------|------|
| `LightTextEditor-win-Setup.exe` | 自带 .NET 运行时，不需要另装任何东西（约 75 MB） |
| `LightTextEditor-win-slim-Setup.exe` | 不含 .NET（约 40 MB）；电脑上没有 .NET 10 桌面运行时时，安装程序会自动下载安装（需要联网，可能请求管理员权限） |

- **推送版本标签**（如 `v1.2.3`、`v1.2.3-beta.1`）：构建后附加到该标签的 Release；还没有 Release 时自动创建草稿，检查后手动发布（发布时不会再构建一次）。
- **在网页上发布 Release 并新建标签**（Draft a new release → 填写新标签 → Publish release）：发布时 GitHub 创建标签，由此触发构建，几分钟后安装包出现在这个 Release 中。
  GitHub 不会为“保存为草稿”触发工作流；想先看到安装包再发布，就先推送标签。
- **为已有的标签补建 / 重建**：Actions → 构建安装包 → Run workflow，填写标签。

版本号必须是 `主.次.修订` 格式（可带 `v` 前缀和预发布后缀），最小 0.0.1。安装包未做代码签名，第一次运行时 Windows SmartScreen 可能提示“未知发布者”。

安装后：.txt、.md 和常见代码文件（列表见 `FileTypes.OpenWith`）的右键“打开方式”菜单中会出现本程序，原来的默认程序不变；
本程序也会出现在“设置 → 应用 → 默认应用”中。安装后第一次启动时有“安装完成”对话框，勾选选框后关闭即打开默认应用设置
（Velopack 的安装程序本身没有可定制的界面）。以后也可以用“文件 → 设为默认程序”。卸载时这些登记一并删除。

## 目录结构

| 路径 | 内容 |
|------|------|
| `src/LightTextEditor.Core` | 与界面无关的逻辑：编码识别（`Text/EncodingDetector.cs`）、原子写入、文件命名、设置 |
| `src/LightTextEditor.App` | Avalonia 界面 |
| `tests/LightTextEditor.Core.Tests` | Core 单元测试 |
| `tests/LightTextEditor.App.Tests` | 无头（headless）界面测试；设置环境变量 `LTE_SCREENSHOT_DIR` 可输出截图 |

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| Ctrl+N / Ctrl+T | 新建标签页 |
| Ctrl+O | 打开 |
| Ctrl+S | 保存（新文档确认文件名和后缀后存入笔记库） |
| Ctrl+Shift+S | 另存为 |
| Ctrl+Alt+S | 全部保存 |
| F2 / 双击标签 | 重命名 |
| Ctrl+W / 鼠标中键 | 关闭标签页 |
| Ctrl+Tab / Ctrl+Shift+Tab | 下一个 / 上一个标签页 |
| Ctrl+1…9 | 切换到第 N 个标签页 |
| Ctrl+F / Ctrl+H | 查找 / 替换 |
| Ctrl+G | 转到行 |
| Ctrl+E | Markdown 源码 / 预览切换（预览中按 Esc 或双击返回编辑） |
| F5 | 插入日期时间 |
| Ctrl+加号 / 减号 / 0、Ctrl+滚轮 | 缩放 |
| Alt+Z | 自动换行 |

## 文件位置

- 笔记库：默认 `文档\LightTextEditor`，可在“文件 → 更改笔记库位置”修改
- 设置、会话与编码记录（`encodings.json`）：`%AppData%\LightTextEditor`（可用环境变量 `LIGHTTEXTEDITOR_CONFIG_DIR` 覆盖）

## 输入法调试日志

输入法（如微软拼音）显示不对时，可以打开调试日志，记录输入法实际发来的内容。没有界面开关，在设置文件里改：

1. 关闭程序（程序运行时会改写设置文件，可能把改动覆盖掉）
2. 打开 `%AppData%\LightTextEditor\settings.json`，把 `"imeDebugLog": false` 改成 `true`（没有这一项就加上 `"imeDebugLog": true,`）
3. 启动程序，用输入法打几个字
4. 日志在 `%AppData%\LightTextEditor\ime-debug.log`（每次启动清空重写，超过 5 MB 后不再写入）

用完把 `imeDebugLog` 改回 `false`。日志里只有输入法相关的内容：组字的拼音、输入的文字和光标位置，不记录文档的其他内容。
