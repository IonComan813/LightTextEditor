using System.Text.RegularExpressions;
using LightTextEditor.Core.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LightTextEditor.App.Services;

/// <summary>Markdown 中引用的一张本地图片：链接地址在源码中的位置、地址文字、图片的完整路径。</summary>
internal sealed record ImageReference(int Start, int Length, bool IsHtml, string FullPath);

/// <summary>迁移时对一张图片的处理：移动（或复制）到新位置。</summary>
internal sealed record ImageTransfer(string Source, string Target, bool Copy);

/// <summary>源码中要改写的链接地址。</summary>
internal sealed record LinkEdit(int Start, int Length, string NewText, string Target);

internal sealed record ImageMigrationPlan(IReadOnlyList<ImageTransfer> Transfers, IReadOnlyList<LinkEdit> Edits)
{
    public bool IsEmpty => Transfers.Count == 0;
}

/// <summary>
/// 把 Markdown 文件移动到别的文件夹（如“移动到笔记库”）时，带上它引用的本地图片：
/// <list type="bullet">
/// <item>文档所在文件夹（含子文件夹，如 assets）里的图片：按原来的相对位置移过去，链接不用改；
///   同一文件夹里的其他 Markdown 文件也引用了这张图片时改为复制，以免它们的图片丢失。</item>
/// <item>文档文件夹以外的图片（../、绝对路径）：复制到新位置的 assets 文件夹，并改写链接。</item>
/// <item>新位置已有同名的不同文件时换个文件名，并改写链接。</item>
/// </list>
/// </summary>
internal static partial class MarkdownImageMigration
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePreciseSourceLocation()
        .Build();

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImageRegex();

    /// <summary>找出文档中引用的、确实存在的本地图片（![](…) 和 &lt;img src="…"&gt;）。</summary>
    public static List<ImageReference> FindLocalImages(string text, string documentDirectory)
    {
        var result = new List<ImageReference>();
        var document = Markdown.Parse(text, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage || link.UrlSpan.IsEmpty || link.Url is null) continue;
            if (Resolve(link.Url, documentDirectory) is { } path)
                result.Add(new ImageReference(link.UrlSpan.Start, link.UrlSpan.Length, false, path));
        }
        foreach (Match m in HtmlImageRegex().Matches(text))
        {
            var url = m.Groups["url"];
            if (Resolve(System.Net.WebUtility.HtmlDecode(url.Value), documentDirectory) is { } path)
                result.Add(new ImageReference(url.Index, url.Length, true, path));
        }
        return result;
    }

    /// <summary>链接地址 → 本地图片的完整路径（网址、不存在的文件、不是图片时为 null）。</summary>
    private static string? Resolve(string url, string documentDirectory)
    {
        url = url.Trim();
        if (url.Length == 0 || url.StartsWith('#')) return null;
        int cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];
        try
        {
            string path;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !(OperatingSystem.IsWindows() && url.Length > 1 && url[1] == ':'))
            {
                if (!uri.IsFile) return null; // http:、data: 等
                path = uri.LocalPath;
            }
            else
            {
                path = Path.GetFullPath(Path.Combine(documentDirectory, Uri.UnescapeDataString(url)));
            }
            return File.Exists(path) && MarkdownAssets.IsImageFile(path) ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or UriFormatException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 规划迁移：文档 <paramref name="documentPath"/>（内容为 <paramref name="text"/>）将移动到 <paramref name="targetDirectory"/>。
    /// </summary>
    public static ImageMigrationPlan Plan(string text, string documentPath, string targetDirectory)
    {
        var oldDir = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        var references = FindLocalImages(text, oldDir);
        if (references.Count == 0) return new ImageMigrationPlan([], []);

        var otherNotes = new Lazy<List<string>>(() => ReadOtherNotes(oldDir, documentPath));
        var targets = new Dictionary<string, string>(StringComparer.FromComparison(PathComparison));
        var reserved = new HashSet<string>(StringComparer.FromComparison(PathComparison));
        var transfers = new List<ImageTransfer>();

        foreach (var source in references.Select(r => r.FullPath).Distinct(StringComparer.FromComparison(PathComparison)))
        {
            bool inside = !MarkdownAssets.NeedsCopy(oldDir, source);
            var wanted = inside
                ? Path.GetFullPath(Path.Combine(targetDirectory, Path.GetRelativePath(oldDir, source)))
                : Path.Combine(targetDirectory, MarkdownAssets.FolderName, Path.GetFileName(source));

            string target;
            if (File.Exists(wanted) && SameContent(wanted, source))
            {
                target = wanted; // 新位置已有一模一样的图片，直接用
            }
            else
            {
                target = wanted;
                var dir = Path.GetDirectoryName(wanted)!;
                var stem = Path.GetFileNameWithoutExtension(wanted);
                var ext = Path.GetExtension(wanted);
                for (int i = 2; File.Exists(target) || Directory.Exists(target) || reserved.Contains(target); i++)
                    target = Path.Combine(dir, $"{stem}-{i}{ext}");
                bool shared = inside && otherNotes.Value.Any(n => n.Contains(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase) ||
                                                                  n.Contains(Uri.EscapeDataString(Path.GetFileName(source)), StringComparison.OrdinalIgnoreCase));
                transfers.Add(new ImageTransfer(source, target, Copy: !inside || shared));
            }
            reserved.Add(target);
            targets[source] = target;
        }

        // 链接只在相对位置变了时才改写
        var edits = new List<LinkEdit>();
        foreach (var r in references)
        {
            var target = targets[r.FullPath];
            var oldRelative = Path.GetRelativePath(oldDir, r.FullPath);
            var newRelative = Path.GetRelativePath(targetDirectory, target);
            bool unchanged = !Path.IsPathRooted(oldRelative) && !oldRelative.StartsWith("..", StringComparison.Ordinal) &&
                             string.Equals(oldRelative, newRelative, PathComparison);
            if (unchanged) continue;
            var url = MarkdownAssets.RelativeUrl(targetDirectory, target);
            edits.Add(new LinkEdit(r.Start, r.Length,
                r.IsHtml ? url.Replace("\"", "%22").Replace("'", "%27") : MarkdownAssets.LinkDestination(url), target));
        }
        return new ImageMigrationPlan(transfers, edits);
    }

    /// <summary>
    /// 执行文件的移动 / 复制，返回没能放到新位置的图片（对应的链接不要改写）。
    /// 移走图片后，原来的 assets 文件夹空了就删掉。
    /// </summary>
    public static HashSet<string> Execute(ImageMigrationPlan plan)
    {
        var failed = new HashSet<string>(StringComparer.FromComparison(PathComparison));
        foreach (var t in plan.Transfers)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(t.Target)!);
                if (t.Copy)
                {
                    File.Copy(t.Source, t.Target);
                }
                else
                {
                    File.Move(t.Source, t.Target);
                    var dir = Path.GetDirectoryName(t.Source)!;
                    if (string.Equals(Path.GetFileName(dir), MarkdownAssets.FolderName, StringComparison.OrdinalIgnoreCase))
                        TryDeleteEmptyDirectory(dir);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(t.Target);
            }
        }
        return failed;
    }

    private static List<string> ReadOtherNotes(string directory, string documentPath)
    {
        var notes = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(documentPath), PathComparison)) continue;
                if (!MarkdownAssets.IsMarkdownExtension(Path.GetExtension(file))) continue;
                if (new FileInfo(file).Length > 8 * 1024 * 1024) continue;
                notes.Add(File.ReadAllText(file));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 读不到时按“没有其他笔记引用”处理
        }
        return notes;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }}
