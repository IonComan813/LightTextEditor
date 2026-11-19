using Avalonia.Platform;
using LightTextEditor.Core.Markdown;
using LightTextEditor.Core.Settings;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Services;

/// <summary>软件自带的 Markdown 示例笔记（Assets/Samples），放在笔记库里。</summary>
internal static class SampleNote
{
    public const string FileName = "Markdown 示例.md";
    private const string ImageFileName = "示例图片.png";

    /// <summary>
    /// 确保笔记库里有示例笔记及其图片，返回笔记路径。已存在的文件不会被覆盖。
    /// </summary>
    public static string Ensure(string libraryPath, AppSettings settings)
    {
        Directory.CreateDirectory(libraryPath);
        var notePath = Path.Combine(libraryPath, FileName);
        if (!File.Exists(notePath))
        {
            string text;
            using (var stream = AssetLoader.Open(new Uri("avares://LightTextEditor/Assets/Samples/markdown-sample.md")))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();
            text = LineEndings.Normalize(text, settings.NewFileLineEnding);
            TextFileIO.Save(notePath, text, TextEncoding.Utf8(settings.ShouldWriteBom(".md")));
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
