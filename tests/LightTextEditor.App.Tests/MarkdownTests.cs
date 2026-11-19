using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightTextEditor.App.Services;
using LightTextEditor.App.Views;
using LightTextEditor.Core.Settings;

namespace LightTextEditor.App.Tests;

public class MarkdownTests
{
    private static (MainWindow Window, string Library) CreateWindow()
    {
        var library = Path.Combine(TestAppBuilder.Root, "md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        App.Settings = new AppSettings { SampleNoteCreated = true, LibraryPath = library, RestoreSession = false };
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Initialize([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, library);
    }

    private static async Task<DocumentTab> OpenAsync(MainWindow window, string path, string content)
    {
        File.WriteAllText(path, content);
        await window.OpenFilesAsync([path]);
        Dispatcher.UIThread.RunJobs();
        return window.ActiveTab!;
    }

    private static void Key(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.Control)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData(".cs", "source.cs")]
    [InlineData(".py", "source.python")]
    [InlineData(".md", "text.html.markdown")]
    [InlineData(".json", "source.json")]
    [InlineData(".txt", null)]
    [InlineData("", null)]
    public void LanguageIsChosenByExtension(string ext, string? scope) =>
        Assert.Equal(scope, SyntaxHighlighting.ScopeFor(ext));

    [AvaloniaFact]
    public async Task CodeFile_GetsSyntaxHighlighting_AndFollowsExtensionChanges()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, Path.Combine(library, "a.cs"), "class A { }");
        Assert.NotNull(tab.Highlighting);
        await window.ChangeExtensionAsync(tab, ".txt"); // 改成纯文本不报错
        await window.ChangeExtensionAsync(tab, ".py");
        Assert.Equal(".py", tab.Doc.Extension);
    }

    [AvaloniaFact]
    public async Task CtrlE_TogglesPreview_ForMarkdownOnly()
    {
        var (window, library) = CreateWindow();
        var txt = await OpenAsync(window, Path.Combine(library, "plain.txt"), "# not markdown");
        Key(window, Avalonia.Input.Key.E);
        Assert.False(txt.IsPreviewing);

        var md = await OpenAsync(window, Path.Combine(library, "note.md"), "# 标题\n\n正文 **粗体**");
        Key(window, Avalonia.Input.Key.E);
        Assert.True(md.IsPreviewing);
        Assert.False(md.Editor.IsVisible);
        Assert.True(md.Preview!.IsVisible);
        Assert.Equal("返回编辑", window.FindControl<Button>("PreviewButton")!.Content);

        for (int i = 0; i < 10; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
        // 渲染出了一级标题块和文字块
        Assert.Contains(md.Preview.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("Heading1Block"));
        Assert.NotEmpty(md.Preview.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>());

        // 切到别的标签再切回来，仍然是预览
        Key(window, Avalonia.Input.Key.D1);
        Assert.False(md.Preview.IsVisible);
        Key(window, Avalonia.Input.Key.D2);
        Assert.True(md.Preview.IsVisible);

        // Esc 回到编辑
        Key(window, Avalonia.Input.Key.Escape, RawInputModifiers.None);
        Assert.False(md.IsPreviewing);
        Assert.True(md.Editor.IsVisible);
    }

    [AvaloniaFact]
    public async Task PastingScreenshot_SavesPngIntoAssets_AndInsertsLink()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, Path.Combine(library, "读书 笔记.md"), "开头\n");
        tab.Editor.CaretOffset = tab.Doc.Document.TextLength;

        using var bitmap = new WriteableBitmap(new PixelSize(20, 10), new Vector(96, 96));
        await window.Clipboard!.SetBitmapAsync(bitmap);
        await window.PasteAsync(tab);

        var assets = Path.Combine(library, "assets");
        var png = Assert.Single(Directory.GetFiles(assets));
        Assert.StartsWith("读书-笔记-", Path.GetFileName(png));
        Assert.Equal(".png", Path.GetExtension(png));
        Assert.Equal(0x89, File.ReadAllBytes(png)[0]); // PNG 文件头
        Assert.EndsWith($"![](assets/{Path.GetFileName(png)})", tab.Doc.Document.Text);
    }

    [AvaloniaFact]
    public async Task PastingText_InMarkdown_PastesTextOnce()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, Path.Combine(library, "t.md"), "");
        await window.Clipboard!.SetTextAsync("你好");
        tab.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Key(window, Avalonia.Input.Key.V);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("你好", tab.Doc.Document.Text);
    }

    [AvaloniaFact]
    public async Task InsertingImageFiles_CopiesOutsideImages_AndLinksInsideOnes()
    {
        var (window, library) = CreateWindow();
        var tab = await OpenAsync(window, Path.Combine(library, "n.md"), "");
        var outside = Path.Combine(TestAppBuilder.Root, "outside pic.png");
        File.WriteAllBytes(outside, [0x89, 0x50]);
        Directory.CreateDirectory(Path.Combine(library, "pics"));
        var inside = Path.Combine(library, "pics", "in.png");
        File.WriteAllBytes(inside, [0x89, 0x50]);

        window.InsertImageFiles(tab, [outside, inside]);
        var text = tab.Doc.Document.Text;
        Assert.Contains("![](assets/outside-pic.png)", text);
        Assert.Contains("![](pics/in.png)", text);
        Assert.True(File.Exists(Path.Combine(library, "assets", "outside-pic.png")));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Screenshot_Preview(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("LTE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var (window, library) = CreateWindow();
            Directory.CreateDirectory(Path.Combine(library, "assets"));
            var shot = Path.Combine(library, "assets", "shot.png");
            using (var bmp = new RenderTargetBitmap(new PixelSize(240, 70)))
            {
                using (var ctx = bmp.CreateDrawingContext())
                    ctx.FillRectangle(new Avalonia.Media.LinearGradientBrush
                    {
                        GradientStops = { new(Avalonia.Media.Color.Parse("#2F6FED"), 0), new(Avalonia.Media.Color.Parse("#F59E0B"), 1) },
                    }, new Rect(0, 0, 240, 70));
                bmp.Save(shot, PngBitmapEncoderOptions.Default);
            }
            var md = await OpenAsync(window, Path.Combine(library, "读书笔记.md"), """
                # 读书笔记

                这是一段**粗体**、*斜体*、`行内代码` 和 [链接](https://example.com) 的混排文字。The quick brown fox.

                - 第一项
                - 第二项
                  - 嵌套项

                - [x] 已完成的任务
                - [ ] 未完成的任务

                > 引用：读书破万卷，下笔如有神。

                | 书名 | 作者 | 评分 |
                |------|------|-----:|
                | 三体 | 刘慈欣 | 9.5 |
                | 活着 | 余华 | 9.4 |

                ```csharp
                var greeting = "你好";
                Console.WriteLine(greeting);
                ```

                ![截图](assets/shot.png)

                ---

                结尾段落。
                """);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"md-source-{theme}.png"));
            Key(window, Avalonia.Input.Key.E);
            for (int i = 0; i < 10; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"md-preview-{theme}.png"));

            var cs = await OpenAsync(window, Path.Combine(library, "Program.cs"), """
                using System;

                // 入口
                public static class Program
                {
                    public static void Main(string[] args)
                    {
                        var name = args.Length > 0 ? args[0] : "世界";
                        Console.WriteLine($"你好，{name}！");
                    }
                }
                """);
            for (int i = 0; i < 5; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"code-{theme}.png"));
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}
