using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using LightTextEditor.App.Services;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Markdown;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Views;

// Markdown：源码 ⇄ 预览切换、预览中的链接、粘贴 / 拖入图片
public partial class MainWindow
{
    private static bool IsMarkdown(DocumentViewModel doc) => MarkdownAssets.IsMarkdownExtension(doc.Extension);

    /// <summary>文档所在目录；未保存的文档在笔记库里（保存时也存到那里）。</summary>
    private static string DocumentDirectory(DocumentViewModel doc) =>
        doc.FilePath is { } p ? Path.GetDirectoryName(p)! : LibraryPath;

    // ───────────────────────── 预览 ─────────────────────────

    private void OnTogglePreviewClick(object? sender, RoutedEventArgs e) => TogglePreview();

    internal void TogglePreview()
    {
        if (_active is null) return;
        if (!_active.IsPreviewing && !IsMarkdown(_active.Doc)) return;
        SetPreview(_active, !_active.IsPreviewing);
    }

    internal void SetPreview(DocumentTab tab, bool on)
    {
        if (on == tab.IsPreviewing) return;
        if (on)
        {
            double ratio = EditorScrollRatio(tab.Editor);
            tab.Preview ??= CreatePreview(tab);
            tab.Preview.Zoom = _vm.Zoom;
            ApplyPageLayout(tab);
            tab.Preview.SetMarkdown(tab.Doc.Document.Text, DocumentDirectory(tab.Doc));
            tab.IsPreviewing = true;
            if (tab == _active)
            {
                FindPanel.Close(); // 查找只在源码中进行
                tab.Editor.IsVisible = false;
                tab.Preview.IsVisible = true;
                tab.Preview.Focus();
            }
            tab.Preview.ScrollToRatio(ratio);
        }
        else
        {
            double ratio = tab.Preview?.ScrollRatio ?? 0;
            tab.IsPreviewing = false;
            if (tab == _active)
            {
                if (tab.Preview is not null) tab.Preview.IsVisible = false;
                tab.Editor.IsVisible = true;
                FocusEditor();
            }
            ScrollEditorToRatio(tab.Editor, ratio);
        }
        if (tab == _active) _vm.IsPreviewing = tab.IsPreviewing;
    }

    private MarkdownPreview CreatePreview(DocumentTab tab)
    {
        var preview = new MarkdownPreview { IsVisible = false, Zoom = _vm.Zoom };
        preview.LinkClicked += uri => _ = OpenLinkAsync(tab, uri);
        preview.TaskToggled += (offset, isChecked) => ToggleTask(tab, offset, isChecked);
        // 预览中双击回到编辑（双击的是按钮、选框等控件时除外，例如快速连点代码块的“换行”按钮）
        preview.DoubleTapped += (_, e) =>
        {
            if (!preview.IsFromInteractiveElement(e.Source)) SetPreview(tab, false);
        };
        // Ctrl + 滚轮缩放（与编辑器相同）；不加 Ctrl 时照常滚动
        preview.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true;
            if (e.Delta.Y == 0) return;
            // 以鼠标所在处为中心缩放
            preview.SetZoom(ClampZoom(_vm.Zoom + (e.Delta.Y > 0 ? 0.1 : -0.1)), e.GetPosition(preview));
            SetZoom(preview.Zoom);
        }, RoutingStrategies.Tunnel);
        EditorHost.Children.Add(preview);
        return preview;
    }

    /// <summary>
    /// 预览中勾选 / 取消勾选任务：改写源码里的“[ ]” / “[x]”（可以撤销，按 Ctrl+S 保存）。
    /// <paramref name="offset"/> 是“[”在源码中的位置；源码对不上时返回 false。
    /// </summary>
    internal bool ToggleTask(DocumentTab tab, int offset, bool isChecked)
    {
        var document = tab.Doc.Document;
        if (offset < 0 || offset + 2 >= document.TextLength ||
            document.GetCharAt(offset) != '[' || document.GetCharAt(offset + 2) != ']' ||
            document.GetCharAt(offset + 1) is not (' ' or 'x' or 'X'))
            return false;
        bool wasChecked = document.GetCharAt(offset + 1) != ' ';
        if (wasChecked != isChecked)
            document.Replace(offset + 1, 1, isChecked ? "x" : " ");
        tab.Preview?.AcceptSourceText(document.Text);
        return true;
    }

    private static double EditorScrollRatio(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        double range = view.DocumentHeight - view.Bounds.Height;
        return range <= 0 ? 0 : Math.Clamp(view.VerticalOffset / range, 0, 1);
    }

    private static void ScrollEditorToRatio(TextEditor editor, double ratio)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var view = editor.TextArea.TextView;
            double range = view.DocumentHeight - view.Bounds.Height;
            if (range > 0) editor.ScrollToVerticalOffset(ratio * range);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 预览中点击链接：网址用浏览器打开；指向本地文本文件（如另一篇笔记）的链接在编辑器中打开；其他本地文件交给系统。
    /// </summary>
    private async Task OpenLinkAsync(DocumentTab tab, Uri uri)
    {
        if (uri.IsAbsoluteUri && uri.Scheme is "http" or "https" or "mailto")
        {
            Shell.Open(uri.AbsoluteUri);
            return;
        }

        string? path;
        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            path = uri.LocalPath;
        }
        else
        {
            var relative = Uri.UnescapeDataString(uri.OriginalString);
            int hash = relative.IndexOf('#');
            if (hash >= 0) relative = relative[..hash];
            if (relative.Length == 0) return; // 页内锚点
            path = Path.GetFullPath(Path.Combine(DocumentDirectory(tab.Doc), relative));
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ShowTransientNotice(tab.Doc, new DocumentNotice("找不到链接的文件", path, NoticeSeverity.Warning, []));
            return;
        }
        if (File.Exists(path) && !MarkdownAssets.IsImageFile(path) && LooksLikeTextFile(path))
            await OpenFilesAsync([path]);
        else
            Shell.Open(path);
    }

    private static bool LooksLikeTextFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[4096];
            int n = fs.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, n) < 0;
        }
        catch (Exception) { return false; }
    }

    // ───────────────────────── 粘贴 / 拖入图片 ─────────────────────────

    /// <summary>
    /// Markdown 文件中粘贴：剪贴板里有图片文件或截图时，保存到 assets 文件夹并插入图片标签；
    /// 有文字时照常粘贴文字（从 Word 等复制的内容同时带图片和文字，此时以文字为准）。
    /// </summary>
    internal async Task PasteAsync(DocumentTab tab)
    {
        if (IsMarkdown(tab.Doc) && Clipboard is { } clipboard)
        {
            try
            {
                if (await TryPasteImageAsync(tab, clipboard)) return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Dialogs.ShowErrorAsync(this, "无法保存图片", e.Message);
                return;
            }
        }
        tab.Editor.Paste();
    }

    private async Task<bool> TryPasteImageAsync(DocumentTab tab, IClipboard clipboard)
    {
        var files = await clipboard.TryGetFilesAsync();
        var images = files?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(MarkdownAssets.IsImageFile).ToList();
        if (images is { Count: > 0 })
        {
            InsertImageFiles(tab, images);
            return true;
        }

        var formats = await clipboard.GetDataFormatsAsync();
        if (formats.Contains(DataFormat.Text)) return false;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null) return false;
        InsertBitmap(tab, bitmap);
        return true;
    }

    /// <summary>把截图存成 PNG 并插入。</summary>
    internal void InsertBitmap(DocumentTab tab, Bitmap bitmap)
    {
        var dir = Path.Combine(DocumentDirectory(tab.Doc), MarkdownAssets.FolderName);
        Directory.CreateDirectory(dir);
        var baseName = tab.Doc.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : null;
        var path = UniquePath(dir, MarkdownAssets.MakeImageFileName(baseName, ".png", DateTime.Now));
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        InsertAtCaret(tab.Editor, ImageLink(tab, path));
    }

    /// <summary>插入已有的图片文件：文档目录以外的图片先复制到 assets 文件夹。</summary>
    internal void InsertImageFiles(DocumentTab tab, IReadOnlyList<string> imagePaths)
    {
        var docDir = DocumentDirectory(tab.Doc);
        var links = new List<string>();
        foreach (var source in imagePaths)
        {
            var target = source;
            if (MarkdownAssets.NeedsCopy(docDir, source))
            {
                var dir = Path.Combine(docDir, MarkdownAssets.FolderName);
                Directory.CreateDirectory(dir);
                var name = LightTextEditor.Core.Library.FileNaming.Sanitize(Path.GetFileNameWithoutExtension(source)).Replace(' ', '-');
                if (name.Length == 0) name = "image";
                target = UniquePath(dir, name + Path.GetExtension(source).ToLowerInvariant());
                File.Copy(source, target);
            }
            links.Add(ImageLink(tab, target));
        }
        InsertAtCaret(tab.Editor, string.Join(tab.Doc.LineEnding.ToNewLineString(), links));
    }

    private static string ImageLink(DocumentTab tab, string imagePath) =>
        MarkdownAssets.ImageMarkdown(MarkdownAssets.RelativeUrl(DocumentDirectory(tab.Doc), imagePath));

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}-{i}{ext}");
        return path;
    }

    private void InsertAtCaret(TextEditor editor, string text)
    {
        int start = editor.SelectionStart;
        editor.Document.Replace(start, editor.SelectionLength, text);
        editor.CaretOffset = start + text.Length;
        FocusEditor();
    }
}
