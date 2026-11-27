using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LightTextEditor.App.Tests;

/// <summary>
/// 把矢量图标渲染成各尺寸 PNG（设置环境变量 LTE_ICON_DIR 时执行），
/// 再合成 Assets/app.ico（48 及以下存为 BMP，更大的存为 PNG，见 tools/make-ico.py）：
///   python3 tools/make-ico.py $LTE_ICON_DIR src/LightTextEditor.App/Assets/app.ico
/// <para>
/// 尺寸覆盖 Windows 常用的缩放比例（100% / 125% / 150% / 200%），系统不必从别的尺寸缩放：
/// 详细信息等小图标 16 / 20 / 24 / 32，任务栏 24 / 32 / 36 / 48，中等图标 48 / 64 / 72 / 96，
/// 大图标 96 / 128，超大图标 256。
/// </para>
/// </summary>
public class IconExport
{
    internal static readonly int[] IcoSizes = [16, 20, 24, 32, 36, 40, 48, 64, 72, 96, 128, 256];

    [AvaloniaFact]
    public void ExportPngs()
    {
        var dir = Environment.GetEnvironmentVariable("LTE_ICON_DIR");
        Application.Current!.TryGetResource("AppIcon", null, out var res);
        var icon = (IImage)res!;
        Assert.Equal(32, icon.Size.Width, 3);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        foreach (var size in IcoSizes)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = bitmap.CreateDrawingContext())
                icon.Draw(ctx, new Rect(icon.Size), new Rect(0, 0, size, size));
            bitmap.Save(Path.Combine(dir, $"icon-{size}.png"));
        }
    }

    /// <summary>Assets/app.ico 包含上面所有尺寸，并且图形撑满格子（原来只占 24×24 里的 16×21，看起来比别的图标小）。</summary>
    [Fact]
    public void AppIco_HasAllSizes_AndTheDrawingFillsTheCell()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LightTextEditor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var data = File.ReadAllBytes(Path.Combine(dir.FullName, "src", "LightTextEditor.App", "Assets", "app.ico"));
        int count = BitConverter.ToUInt16(data, 4);
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            int size = data[e] == 0 ? 256 : data[e];
            sizes.Add(size);
            int length = BitConverter.ToInt32(data, e + 8), offset = BitConverter.ToInt32(data, e + 12);
            if (size is not (24 or 48)) continue;
            // 48 及以下是 32 位 BMP：40 字节头 + 自下而上的 BGRA
            Assert.False(data[offset] == 0x89, "48 及以下应为 BMP");
            int top = size, bottom = -1, left = size, right = -1;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (data[offset + 40 + ((size - 1 - y) * size + x) * 4 + 3] > 24)
                    {
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        left = Math.Min(left, x); right = Math.Max(right, x);
                    }
            Assert.True(bottom - top + 1 >= size * 0.9, $"{size}：高 {bottom - top + 1}");
            Assert.True(right - left + 1 >= size * 0.75, $"{size}：宽 {right - left + 1}");
            Assert.True(Math.Abs(top - (size - 1 - bottom)) <= 1, $"{size}：上下不居中（上空 {top}，下空 {size - 1 - bottom}）");
        }
        Assert.Equal(IcoSizes, sizes);
    }
}
