using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LightTextEditor.App.Tests;

/// <summary>
/// 把矢量图标渲染成各尺寸 PNG（设置环境变量 LTE_ICON_DIR 时执行），
/// 再用 ImageMagick 合成 Assets/app.ico：
///   convert icon-16.png icon-24.png icon-32.png icon-48.png icon-64.png icon-256.png app.ico
/// </summary>
public class IconExport
{
    [AvaloniaFact]
    public void ExportPngs()
    {
        var dir = Environment.GetEnvironmentVariable("LTE_ICON_DIR");
        Application.Current!.TryGetResource("AppIcon", null, out var res);
        var icon = (IImage)res!;
        Assert.Equal(32, icon.Size.Width, 3);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = bitmap.CreateDrawingContext())
                icon.Draw(ctx, new Rect(icon.Size), new Rect(0, 0, size, size));
            bitmap.Save(Path.Combine(dir, $"icon-{size}.png"));
        }
    }
}
