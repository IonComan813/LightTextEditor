using Avalonia.Platform;
using LightTextEditor.Core.Markdown;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Services;

/// <summary>
/// 软件自带的示例（Assets/Samples）：Markdown 示例和 HTML 示例，放在笔记库里，与普通笔记一样可以修改、删除。
/// </summary>
internal static class SampleNote
{
    public const string FileName = "Markdown 示例.md";
    public const string HtmlFileName = "HTML 示例.html";
    private const string ImageFileName = "示例图片.png";

    /// <summary>
    /// 确保笔记库里有 Markdown 示例及其图片，返回笔记路径。已存在的文件不会被覆盖。
    /// </summary>
    public static string Ensure(string libraryPath, AppSettings settings) =>
        EnsureSample(libraryPath, settings, FileName, "markdown-sample.md");

    /// <summary>确保笔记库里有 HTML 示例及其图片，返回文件路径。已存在的文件不会被覆盖。</summary>
    public static string EnsureHtml(string libraryPath, AppSettings settings) =>
        EnsureSample(libraryPath, settings, HtmlFileName, "html-sample.html");

    private static string EnsureSample(string libraryPath, AppSettings settings, string fileName, string assetName)
    {
        Directory.CreateDirectory(libraryPath);
        var notePath = Path.Combine(libraryPath, fileName);
        if (!File.Exists(notePath))
        {
            string text;
            using (var stream = AssetLoader.Open(new Uri("avares://LightTextEditor/Assets/Samples/" + assetName)))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();
            text = LineEndings.Normalize(text, settings.NewFileLineEnding);
            var extension = Path.GetExtension(fileName);
            TextFileIO.Save(notePath, text, TextEncoding.Utf8(settings.ShouldWriteBom(extension)));
        }

        var imagePath = Path.Combine(libraryPath, MarkdownAssets.FolderName, ImageFileName);
        if (!File.Exists(imagePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            using var source = AssetLoader.Open(new Uri("avares://LightTextEditor/Assets/Samples/sample-image.png"));
            using var target = File.Create(imagePath);
            source.CopyTo(target);
        }
        return notePath;
    }
}
