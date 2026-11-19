# LightTextEditor 可行性评估与 Roadmap

> 评估日期：2026-11-18（2026-11-19 按决定更新：取消自动保存；采用 Avalonia 12 + FluentAvalonia 3）
> 技术栈：.NET 10 (LTS) + Avalonia UI
> 结论：**整体可行**。7 项需求都能找到成熟的开源库支撑，没有"做不出来"的硬伤；有两处需要调整原始设想（第 5 条编码声明的落地方式、第 7 条 Windows Hello 不能单独充当加密），另有 4 个风险点要在第一周用技术验证（Spike）确认。

---

## 一、逐项评估

| # | 需求 | 可行性 | 难度 | 主要依赖 | 关键风险 / 调整 |
|---|------|--------|------|----------|-----------------|
| 1 | 指定目录存储、打开即写、多标签 | ✅ 高 | 低 | FluentAvalonia `TabView` 或 Avalonia 12 自带 TabView | 需要定义"库外文件"如何处理 |
| 2 | 近期文件列表、时间排序、全文搜索 | ✅ 高 | 中 | SQLite + FTS5 (`Microsoft.Data.Sqlite`) | 中文分词：用 `trigram` 分词器 |
| 3 | ~~自动保存~~ 手动保存 + 内置版本历史 | ✅ 高 | 中 | SQLite、DiffPlex | 原子写入、保留策略、与外部修改冲突 |
| 4 | 代码高亮 / MD 源码⇄渲染 / 粘贴图片 | ✅ 高 | 中 | AvaloniaEdit + AvaloniaEdit.TextMate、Markdown.Avalonia | 中文输入法 (IME)、MD 渲染完整度 |
| 5 | 编码声明 + 编码检测流程 | ⚠️ 部分可行 | 中 | `UTF.Unknown`、`System.Text.Encoding.CodePages` | **不建议往文件内容里写声明**，改为元数据 + BOM 策略 |
| 6 | Fluent Design / Windows 风 | ✅ 高 | 低~中 | Avalonia FluentTheme、FluentAvalonia、Mica | FluentAvalonia 对 Avalonia 12 的支持需确认 |
| 7 | 本地加密 + Windows Hello | ✅ 可行（需改设计） | 高 | AES-GCM、DPAPI / NCrypt (Hello 密钥)、Argon2id | `UserConsentVerifier` 只验证身份、不产生密钥 |

### 1. 指定目录存储、打开即写、多标签

**可行，体验上的关键是"不让用户思考文件名和位置"。**

- 设定一个"笔记库"目录（默认 `文档\LightTextEditor\`，可在设置里改，可指向 OneDrive 同步目录）。
- `Ctrl+N` 直接在库里创建文件，文件名用时间戳（如 `2026-11-18 1430.md`）；标签页双击可重命名。可选：首行是 `# 标题` 时提示用"标题"作为文件名，但不要静默自动改名（会让外部引用失效）。
- 新文件默认 `.md`；在状态栏切换语言模式时，同时修改扩展名。
- 启动时恢复上次打开着的（已保存的）文件。关闭有未保存更改的标签页或窗口时，询问"保存 / 不保存 / 取消"。
- **库外文件**（从资源管理器打开的 `.cs`、`.txt` 等）：照常编辑、照常进入"近期文件"，Ctrl+S 写回原位置。
- 标签控件：Avalonia 11 下用 FluentAvalonia 的 `TabView`；Avalonia 12 自带 `TabView`/`TabbedPage`，也有第三方 `TabView.Avalonia`（支持拖拽、拆分窗口）。标签放进标题栏需要 `ExtendClientAreaToDecorationsHint`。

### 2. 近期文件、时间排序、全文搜索

**可行。**

- 用一个 SQLite 数据库（`%LocalAppData%\LightTextEditor\index.db`）记录：路径、最后打开/修改时间、编码、语言、是否加密、是否置顶。
- 全文搜索用 SQLite **FTS5**（`Microsoft.Data.Sqlite` 自带的 `e_sqlite3` 已包含 FTS5）。
  - **注意中文**：FTS5 默认的 `unicode61` 分词器会把一整句中文当成一个词，搜不到子串。应使用 **`trigram` 分词器**（SQLite ≥ 3.34），支持任意子串匹配；少于 3 个字的查询回退到 `LIKE`。
  - 备选：Lucene.NET + 中文分词器，功能更强但更重，本项目不需要。
- 每次保存后增量更新索引（后台队列）。
- 搜索结果显示片段高亮（FTS5 的 `snippet()` / `highlight()`）。
- 加密文件**不能**以明文进入索引，见第 7 条。

### 3. 手动保存 + 版本历史

> **已决定：不做自动保存。** 习惯随时按 Ctrl+S；自动保存会把临时的、写到一半的内容也存下来。

**可行，实现上要注意数据安全。**

- **手动保存**：Ctrl+S。未命名文档第一次保存时弹窗确认文件名和后缀（默认首行文字或时间 + `.txt`，可修改），存入笔记库，不询问位置；关闭未保存的标签页 / 窗口时同样如此。有未保存更改时，标签上的 × 显示为 ●，鼠标移到 ● 上再变回 ×（与 Windows 11 记事本一致）。
- **原子写入**：先写到同目录临时文件，再 `File.Replace` / `File.Move(overwrite)`，避免断电或崩溃导致文件只写了一半。
- **外部修改**：`FileSystemWatcher` 监听已打开文件；若磁盘内容被别的程序改了，而编辑器里也有未保存修改，弹出 InfoBar 让用户选择（并把双方都存进历史，保证什么都不丢）。
- **版本历史**：
  - 快照存 SQLite（或 `history/` 目录下按内容哈希存放的压缩 blob），触发条件：距上次快照 ≥ N 分钟且有改动、关闭文件时、恢复旧版本前、编码转换前。
  - 保留策略：24 小时内全部保留 → 7 天内每小时 1 份 → 30 天内每天 1 份（可配置）。
  - 存储：Deflate/Brotli 压缩全量快照即可（文本很小）；没必要一开始就做增量 diff 存储。
  - 界面：历史列表 + 差异对比（`DiffPlex` 计算差异，用 AvaloniaEdit 并排/内联显示）+ "恢复此版本"。
  - 不建议内嵌 Git（LibGit2Sharp）：重、概念多，对普通笔记用户没有收益。

### 4. 代码高亮、Markdown 双模式、粘贴图片

**可行。**

- **编辑器核心**：`Avalonia.AvaloniaEdit`（AvalonEdit 的 Avalonia 移植，官方维护，已有 12.0.0 版本）。自带查找替换面板、折叠、行号、大文件性能尚可（几 MB 没问题；上百 MB 需要"大文件只读模式"）。
- **语法高亮**：`AvaloniaEdit.TextMate` + `TextMateSharp.Grammars`，直接使用 VS Code 的 TextMate 语法和主题（Dark+/Light+ 等），覆盖几十种语言，按扩展名自动选择。这正是"现成库"。
- **Markdown 源码 ⇄ 渲染切换**（如 `Ctrl+E`）：
  - 渲染用 `Markdown.Avalonia`（原生控件渲染，支持表格、代码块高亮、相对路径图片）；备选 `LiveMarkdown.Avalonia`。
  - 两者都不支持 LaTeX 公式、Mermaid 等扩展。如需这些，只能走 WebView 路线（Avalonia 官方 WebView 属于付费的 Accelerate；开源方案基于 CEF，体积 +100 MB），**建议 v1 不做**。
  - 切换时尽量同步滚动位置（按源码行号映射到渲染块）。
  - 渲染模式为只读。"所见即所得编辑"（类似 Typora）是另一个数量级的工程，**不建议**纳入本项目。
- **Ctrl+V 粘贴图片**：
  - 在编辑器 `TextArea` 上用隧道路由 (Tunnel) 拦截 `Ctrl+V` 和右键"粘贴"命令；剪贴板中有图片时接管，否则交给默认粘贴。
  - 读取图片：Avalonia 12 提供 `DataFormat.Bitmap` / `TryGetBitmapAsync`；如果留在 Avalonia 11，则在 Windows 上需要自己通过 Win32 互操作读取 `PNG` / `CF_DIBV5` 格式（约 200 行代码）。另外应处理"在资源管理器里复制了图片文件"（文件列表格式）的情况。
  - 保存到 `<md 所在目录>/assets/<文件名>-<时间戳>.png`，在光标处插入 `![](assets/xxx.png)`，使用相对路径保证文件夹搬走后仍可用。
  - 也可顺带支持拖放图片进编辑器，代码基本相同。

### 5. 编码格式声明与检测

**检测流程完全可行；"给所有文件加编码声明"不建议照原样实现。**

**为什么不建议往文件里写声明：**

- `.txt`、`.md` 没有任何标准的"文件内编码声明"语法，写进去就是正文的一部分，别的软件会原样显示。
- 只有少数格式有原生声明：XML (`<?xml encoding=…?>`)、HTML (`<meta charset>`)、CSS (`@charset`)、Python (`# -*- coding -*-`)。JSON 不允许注释；Shell 脚本第一行必须是 `#!`；在代码里插入额外行会污染 Git diff。
- 对文本文件来说，**唯一通用的"自描述"手段是 BOM**，它本身就是编码声明。

**建议的方案：**

1. **本软件新建的文件一律 UTF-8**。
   - `.txt` / `.md`：默认写 **UTF-8 BOM**（可在设置中关闭）。这就是"编码格式声明"，所有 Windows 软件都认得。
   - 代码文件：默认**不带 BOM**（BOM 会破坏 Shell 脚本、PHP、部分 JSON 解析器和编译器）。
   - 若目录下存在 `.editorconfig` 的 `charset`，以它为准。
2. **打开已有文件时的检测顺序**（与你的设想一致，补充两步）：
   1. BOM（UTF-8 / UTF-16 LE/BE / UTF-32）
   2. 本软件元数据库里已"确认"过的编码 / `.editorconfig`
   3. 严格 UTF-8 校验（`new UTF8Encoding(false, throwOnInvalidBytes: true)`）；纯 ASCII 直接按 UTF-8 处理
   4. 无 BOM UTF-16 的启发式判断（大量 `0x00` 交替出现）
   5. 统计猜测：`UTF.Unknown`（uchardet 的 .NET 移植）；需要先注册 `CodePagesEncodingProvider` 才能用 GBK / Big5 / Shift-JIS
   6. 置信度低于阈值 → 标记为"**待确认**"
3. **"待确认"状态的行为**：
   - 顶部显示 InfoBar："检测为 GBK（置信度 62%），显示是否正常？ [正确] [换一个编码▾] [转换为 UTF-8]"。
   - 未确认前，保存时**保持原编码原样写回**，绝不静默转码；若当前编码无法无损表示内容（解码出现 `U+FFFD`），保存前提示改用 UTF-8。
   - 用户点"正确"→ 在**元数据库**里记录"此文件编码 = GBK（已确认）"，下次直接使用，不再改动文件内容。
   - 用户点"转换为 UTF-8"→ 先存一份历史快照，再以 UTF-8（按上面的 BOM 规则）重写。这才是一劳永逸的"打标签"。
4. 状态栏始终显示当前编码，可随时"以其他编码重新打开"或"以其他编码保存"。

> 关于用 NTFS 备用数据流 (ADS) 存编码标签：技术上可行，但复制到 U 盘、上传网盘、Git 提交时都会丢失，不推荐作为主方案。

### 6. Fluent Design 界面

**可行。**

- Avalonia 自带 `FluentTheme`（明暗主题，跟随系统）。
- **FluentAvalonia** 补齐 WinUI 风格控件：`TabView`、`NavigationView`、`InfoBar`、`ContentDialog`、`CommandBar`、`SettingsExpander`，以及系统强调色。
- Win11 下窗口背景用 **Mica**（`TransparencyLevelHint = Mica`），标题栏与标签栏融合（类似 Win11 记事本）。
- 图标用 `FluentIcons.Avalonia`（Microsoft Fluent UI System Icons，MIT 协议）。注意 Segoe Fluent Icons 字体只能使用系统自带的，不能随软件分发。
- 字体：界面 Segoe UI Variable，编辑器默认 Cascadia Code / Consolas + 微软雅黑回退。

**版本选择（需在 M0 决定）：**

| 方案 | 优点 | 缺点 |
|------|------|------|
| A. Avalonia **12.x** + FluentAvalonia **3.x**（目前为 preview） | 新剪贴板 API 直接支持图片；自带 TabView；长期方向 | FluentAvalonia 3 尚未发布正式版，需评估稳定性 |
| B. Avalonia **11.3** + FluentAvalonia **2.5**（稳定） | 组合成熟 | 剪贴板图片需自写 Win32 互操作；之后还要迁移到 12 |
| C. Avalonia **12.x** + 只用自带 FluentTheme | 依赖最少 | InfoBar、SettingsExpander 等需自己实现 |

**已决定：方案 A。** 实际开工时 FluentAvalonia 已发布 **3.1.0 正式版**（依赖 Avalonia 12.1），当前使用 Avalonia 12.1.6 + FluentAvalonia 3.1.0 + AvaloniaEdit 12.0.0。

### 7. 本地加密与 Windows Hello

**可行，但原始设想需要修正：`UserConsentVerifier` 本身不能用于加密。**

- `UserConsentVerifier.RequestVerificationAsync()` 只返回"验证通过/未通过"，**不产生任何密钥**。如果只用它在打开前"拦一下"，文件在磁盘上仍是明文，任何人用别的软件都能直接打开，只算 UI 锁，不算加密。
- 正确做法是**信封加密**：
  1. 生成随机 256 位**数据密钥 (DEK)**，文件内容用 **AES-256-GCM**（`System.Security.Cryptography.AesGcm`）加密。
  2. DEK 用以下方式各"包装"一份，存放在保险库头部：
     - **Windows Hello**：通过 `KeyCredentialManager` 或 NCrypt 的 "Microsoft Passport Key Storage Provider" 创建绑定 TPM 的 Hello 密钥，解锁时弹出 Hello（PIN/指纹/人脸）。KeePassWinHello 等项目已验证过这条路。
     - **恢复密码**：用户设置的密码经 **Argon2id** 派生的密钥。**必须有**：重置 Hello PIN、清除 TPM、重装系统、换电脑都会让 Hello 密钥永久丢失。
  3. 解锁后的 DEK 只放内存，锁定或闲置超时就清零。
  - 更简单的折中：DEK 用 **DPAPI**（`ProtectedData`，CurrentUser）保护 + `UserConsentVerifier` 作为解锁门禁。可以防其他 Windows 用户和硬盘被拆，但防不了以当前用户身份运行的恶意程序。可作为 v1，再升级到 Hello 密钥方案。
- **容易遗漏的泄露点**（加密文件都要覆盖）：版本历史快照、全文索引（加密文件不进索引，或整个索引库用 SQLCipher 加密）、保存时的临时文件、粘贴进来的图片 `assets/`、近期文件列表里的标题/预览。
- 桌面 (Win32) 应用调用 Hello 时，要传入窗口句柄（`UserConsentVerifierInterop` / `NCRYPT_WINDOW_HANDLE_PROPERTY`），否则验证框可能出现在主窗口后面。项目目标框架设为 `net10.0-windows10.0.19041.0` 才能直接使用 WinRT API。
- 先想清楚**防什么**：最有意义的场景是"笔记库放在 OneDrive / 网盘同步"或"多人共用电脑"；如果只是防笔记本丢失，开启 BitLocker 就够了。
- 这些 API 仅限 Windows：放到 `Platform.Windows` 项目里，通过接口抽象，将来 macOS 可用 Touch ID + Keychain 实现。

---

## 二、建议架构

```
LightTextEditor.sln
├─ src/
│  ├─ LightTextEditor.App/              Avalonia UI（视图、ViewModel、主题）
│  ├─ LightTextEditor.Core/             无 UI 依赖：文档模型、编码检测、原子写入、
│  │                                    版本历史、搜索索引、加密格式
│  └─ LightTextEditor.Platform.Windows/ Hello / DPAPI / 剪贴板互操作 / 跳转列表
└─ tests/
   └─ LightTextEditor.Core.Tests/       xUnit，重点测编码检测、原子写入、加解密
```

- MVVM：`CommunityToolkit.Mvvm`；依赖注入：`Microsoft.Extensions.DependencyInjection`；日志：Serilog。
- 数据：一个 SQLite 文件（元数据 + FTS5 + 历史），`Microsoft.Data.Sqlite` + 少量手写 SQL（或 Dapper）。
- 打包/更新：Velopack（自动更新、无需管理员权限）；需要商店上架时再做 MSIX。
- 暂不追求 Native AOT：TextMateSharp 依赖原生 Oniguruma 库，AOT 兼容性需另行验证。

---

## 三、分步骤 Roadmap

时间按 **1 名熟悉 .NET 的开发者全职**估算；业余时间开发请乘以 2.5~3。

### M0 · 技术验证 Spike（1~2 周）——决定能否开工

每项写一个最小 Demo，全部通过再进入 M1：

- [x] AvaloniaEdit 中**中文输入法**：候选框位置正确、组字过程正常（已人工测试，没有问题）
- [ ] AvaloniaEdit.TextMate 高亮 C#/Python/JSON，明暗主题切换
- [ ] Markdown.Avalonia 渲染含表格、代码块、相对路径图片的文档
- [ ] 从剪贴板读取截图（Win+Shift+S）并保存为 PNG
- [ ] 在 Avalonia 窗口中调用 `UserConsentVerifier`（带窗口句柄），以及 NCrypt Hello 密钥加解密
- [ ] FluentAvalonia `TabView` 放进标题栏 + Mica
- [x] **决定 Avalonia 11 / 12 及 FluentAvalonia 版本** → Avalonia 12.1 + FluentAvalonia 3.1

### M1 · 骨架与核心编辑（2~3 周）—— MVP 开始 ✅ 已完成（待 Windows 实机验证）

- [x] 解决方案结构（Core / App / 两个测试项目）、设置存储（JSON）
- [x] Fluent 主窗口：标题栏标签页、Mica、明暗主题切换、菜单栏、状态栏（行列、字符数、缩放、换行符、编码）
- [x] 笔记库目录；`Ctrl+N` 即写；第一次 `Ctrl+S` 确认文件名和后缀（默认首行 / 时间 + `.txt`）后存入笔记库；打开 / 另存为 / 重命名；拖放打开文件
- [x] 软件内更改后缀（状态栏 / 文件菜单），已保存的文件直接在磁盘上重命名
- [x] 手动保存 + 原子写入；未保存标记（● ↔ ×）；关闭时询问保存；会话恢复；单实例
- [x] 外部修改检测（未修改则自动重新加载，有修改则提示；文件被删除时标为未保存）
- [x] 查找替换（AvaloniaEdit 自带中文面板）、转到行、常用快捷键、Ctrl+滚轮缩放
- [x] 新文件统一 UTF-8 写入（.txt/.md 带 BOM，代码文件不带）；读取时 BOM → 严格 UTF-8 → GB18030 回退并提示确认；状态栏可换编码重新打开 / 以其他编码保存
- [x] 保存前检查编码能否无损表示全部字符，不能则建议改用 UTF-8
- [x] 自绘标题栏按钮（替换 Avalonia 12 默认的标题文字 + 四个按钮）：高度与标签栏一致、中文提示、去掉全屏按钮
- [ ] **待验证（只能在 Windows 上做）**：Mica 效果、标题栏拖动区域、Windows 11 最大化按钮的贴靠布局

### M2 · 代码高亮与 Markdown（2~3 周）

- [ ] TextMate 语法高亮，按扩展名自动选择语言，状态栏可手动切换
- [ ] Markdown 源码 ⇄ 渲染切换（`Ctrl+E`），滚动位置同步
- [ ] `Ctrl+V` / 拖放图片 → `assets/` + 插入 `![]()`
- [ ] 渲染模式下点击链接、打开图片

> **到这里就是一个可日常使用的 MVP（约 5~8 周）。**

### M3 · 编码检测（1~2 周）

- [ ] BOM → 元数据/editorconfig → 严格 UTF-8 → UTF-16 启发式 → UTF.Unknown 检测管线
- [ ] "待确认"InfoBar、以其他编码重新打开、转换为 UTF-8
- [x] 无损保护：无法无损编码时提示改用 UTF-8（M1 已完成）
- [ ] 准备一组 GBK/Big5/Shift-JIS/UTF-16/短文本测试样本，写单元测试

### M4 · 近期文件与全文搜索（1~2 周）

- [ ] 元数据库：近期文件、置顶、按打开时间/修改时间/名称排序
- [ ] FTS5 `trigram` 索引，后台增量更新，首次启动扫描笔记库
- [ ] 搜索面板（类似 VS Code `Ctrl+Shift+F`）：片段高亮、点击跳转到对应行

### M5 · 版本历史（2 周）

- [ ] 快照触发（每次手动保存时、恢复旧版本前、重新加载前）与保留策略
- [ ] 历史面板：时间线、差异视图（DiffPlex）、恢复、另存为
- [ ] 外部修改冲突处理接入历史（两边都保留）

### M6 · 本地加密（2~3 周）

- [ ] 加密文件格式（魔数 + 版本 + 包装后的密钥 + nonce + 密文 + tag），扩展名如 `.ltxe`
- [ ] v1：DPAPI + `UserConsentVerifier` 门禁；或直接实现 Hello 密钥（NCrypt）+ Argon2id 恢复密码
- [ ] 锁定/闲置自动锁定，内存中清零密钥
- [ ] 覆盖泄露点：历史、索引、临时文件、`assets/` 图片、近期列表预览
- [ ] 密钥丢失与恢复流程的端到端测试

### M7 · 打磨与发布（2 周）

- [ ] 设置页（SettingsExpander 风格）、快捷键自定义
- [ ] 大文件模式、性能分析（启动时间目标 < 1 秒）
- [ ] Velopack 安装包与自动更新、文件关联、跳转列表 (Jump List)
- [ ] 可访问性检查（键盘导航、高对比度）

**合计：约 13~20 周（全职）**；MVP（M0~M2）约 5~8 周。

---

## 四、风险清单

| 风险 | 影响 | 可能性 | 应对 |
|------|------|--------|------|
| AvaloniaEdit 中文 IME 体验差 | 高 | 中 | M0 第一项验证；必要时向上游提 issue/PR |
| FluentAvalonia 与 Avalonia 12 兼容性 | 中 | 低 | 已解决：FluentAvalonia 3.1 正式版支持 Avalonia 12 |
| Markdown 渲染功能不全（公式、Mermaid） | 中 | 高 | v1 明确不支持；后续可选 WebView 插件 |
| 加密密钥丢失导致数据无法恢复 | **极高** | 低 | 强制设置恢复密码；导出恢复密钥 |
| 编码误判导致保存后乱码 | 高 | 中 | "待确认"状态不转码 + 无损检查 + 历史快照 |
| 保存时写坏文件 | 高 | 低 | 原子写入 + 写前快照 |
| 跨平台需求 | 低 | — | 第 6、7 条仅限 Windows；已通过 Platform 层隔离 |

---

## 五、与原始设想的差异汇总

1. **第 5 条**：不往文件内容里写编码声明；改为"新文件 UTF-8（txt/md 带 BOM）+ 外部元数据记录已确认的编码 + 提供一键转 UTF-8"。
2. **第 7 条**：Windows Hello 只能作为"解锁方式"，真正加密靠 AES-GCM + 信封密钥；必须设计恢复密码。
3. **第 4 条**：Markdown 渲染模式为只读预览，不做所见即所得；公式/图表 v1 不支持。
4. **第 3 条**：取消自动保存，改为手动保存（Ctrl+S），未保存时标签显示 ●。
