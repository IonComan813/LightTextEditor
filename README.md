# LightTextEditor

基于 [Avalonia](https://avaloniaui.net/) 的轻量文本编辑器，Fluent Design 风格。

- 多标签页，标签栏融入标题栏（Windows 11 上使用 Mica 背景；自绘最小化/最大化/关闭按钮，带中文提示）
- **保存到笔记库不用选位置**：新文档第一次保存时只确认文件名和后缀（默认取首行文字或时间 + `.txt`），都可修改
- 状态栏或“文件 → 更改后缀”可以把文件直接转换为其他后缀（如 `.md`、`.py`）
- 手动保存；有未保存更改时标签上显示 ●，鼠标移上去变成 ×
- 读取时识别编码：BOM → 记住的编码 / `.editorconfig` → 无 BOM 的 UTF-16 → UTF-8 → 统计检测（GBK、Big5、Shift-JIS、EUC-KR 等）
  - 拿不准时顶部提示“请确认编码”：[✓]（记住，下次直接用）[换一个编码▾]（试过的编码显示为灰色）；点 ✓ 后再询问是否转换为 UTF-8
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
- “查看 → 居中布局”：正文（含 Markdown 预览）显示在窗口中间固定宽度的页面上，类似 Word；不开时代码和纯文本铺满窗口，Markdown 靠左、宽度不超过页面宽度
- 外部修改检测、会话恢复、单实例（从资源管理器打开的文件会进入已打开的窗口）

开发计划见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/LightTextEditor.App
```

```bash
dotnet test
```

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
