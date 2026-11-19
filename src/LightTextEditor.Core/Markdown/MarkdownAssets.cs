using LightTextEditor.Core.Library;

namespace LightTextEditor.Core.Markdown;

/// <summary>Markdown 文件中插入图片：图片存放位置、文件名、链接写法。</summary>
public static class MarkdownAssets
{
    /// <summary>图片存放的子文件夹（相对于 Markdown 文件所在目录）。</summary>
    public const string FolderName = "assets";

    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"];

    public static bool IsMarkdownExtension(string extension) =>
        MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 粘贴截图时生成的文件名："笔记名-20261119-143005.png"。
    /// 空格换成连字符，链接里就不需要转义。
    /// </summary>
    public static string MakeImageFileName(string? documentBaseName, string extension, DateTime now)
    {
        var baseName = string.IsNullOrWhiteSpace(documentBaseName) ? "image" : FileNaming.Sanitize(documentBaseName);
        if (baseName.Length == 0) baseName = "image";
        baseName = baseName.Replace(' ', '-');
        if (!extension.StartsWith('.')) extension = "." + extension;
        return $"{baseName}-{now:yyyyMMdd-HHmmss}{extension.ToLowerInvariant()}";
    }

    /// <summary>
    /// 决定一张已有图片在文档中如何引用：
    /// 已经位于文档目录（或其子目录）内的图片直接引用；否则需要复制到 assets 文件夹。
    /// </summary>
    public static bool NeedsCopy(string documentDirectory, string imagePath)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(documentDirectory), Path.GetFullPath(imagePath));
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel);
    }

    /// <summary>从文档目录指向图片的相对路径，使用正斜杠。</summary>
    public static string RelativeUrl(string documentDirectory, string imagePath) =>
        Path.GetRelativePath(Path.GetFullPath(documentDirectory), Path.GetFullPath(imagePath)).Replace('\\', '/');

    /// <summary>
    /// 生成图片的 Markdown 写法。路径里有空格或括号时用尖括号包起来（CommonMark 语法），避免链接断掉。
    /// </summary>
    public static string ImageMarkdown(string url, string alt = "")
    {
        alt = alt.Replace("[", "\\[").Replace("]", "\\]");
        return $"![{alt}]({LinkDestination(url)})";
    }

    /// <summary>链接地址的写法：有空格或括号时用尖括号包起来。</summary>
    public static string LinkDestination(string url)
    {
        bool needsAngle = url.IndexOfAny([' ', '(', ')', '<', '>']) >= 0;
        return needsAngle ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;
    }
}
