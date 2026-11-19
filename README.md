# LightTextEditor

基于 [Avalonia](https://avaloniaui.net/) 的轻量文本编辑器，Fluent Design 风格。

- 多标签页，标签栏融入标题栏（Windows 11 上使用 Mica 背景；自绘最小化/最大化/关闭按钮，带中文提示）
- **保存到笔记库不用选位置**：新文档第一次保存时只确认文件名和后缀（默认取首行文字或时间 + `.txt`），都可修改
- 状态栏或“文件 → 更改后缀”可以把文件直接转换为其他后缀（如 `.md`、`.py`）
- 手动保存；有未保存更改时标签上显示 ●，鼠标移上去变成 ×
- 读取时识别编码（BOM → 严格 UTF-8 → 回退 GB18030 并提示确认），保存前检查编码能否无损保存
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
| `src/LightTextEditor.Core` | 与界面无关的逻辑：编码识别、原子写入、文件命名、设置 |
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
| F5 | 插入日期时间 |
| Ctrl+加号 / 减号 / 0、Ctrl+滚轮 | 缩放 |
| Alt+Z | 自动换行 |

## 文件位置

- 笔记库：默认 `文档\LightTextEditor`，可在“文件 → 更改笔记库位置”修改
- 设置与会话：`%AppData%\LightTextEditor`（可用环境变量 `LIGHTTEXTEDITOR_CONFIG_DIR` 覆盖）
