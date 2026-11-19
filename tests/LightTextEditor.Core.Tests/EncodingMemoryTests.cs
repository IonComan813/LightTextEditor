using System.Text;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class EncodingMemoryTests
{
    static EncodingMemoryTests() => TextEncoding.EnsureCodePagesRegistered();

    [Fact]
    public void Remember_PersistsAcrossInstances()
    {
        using var dir = new TempDir();
        var store = dir.File("encodings.json");
        var file = dir.File("old.txt");

        new EncodingMemory(store).Remember(file, TextEncoding.FromCodePage(950));

        var reloaded = new EncodingMemory(store);
        Assert.Equal(950, reloaded.Get(file)!.CodePage);
        Assert.Null(reloaded.Get(dir.File("other.txt")));
    }

    [Fact]
    public void SelfDescribingEncodings_AreNotRemembered()
    {
        var memory = new EncodingMemory(null);
        memory.Remember("/x/a.txt", TextEncoding.Gb18030(true));
        memory.Remember("/x/a.txt", TextEncoding.Utf8(withBom: true));
        Assert.Null(memory.Get("/x/a.txt"));

        memory.Remember("/x/b.txt", TextEncoding.FromCodePage(1200, hasBom: true));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void Move_FollowsTheFile()
    {
        var memory = new EncodingMemory(null);
        memory.Remember("/x/a.txt", TextEncoding.FromCodePage(932));
        memory.Move("/x/a.txt", "/y/b.txt");
        Assert.Null(memory.Get("/x/a.txt"));
        Assert.Equal(932, memory.Get("/y/b.txt")!.CodePage);
    }

    [Fact]
    public void Capacity_DropsLeastRecentlyUsed()
    {
        var memory = new EncodingMemory(null);
        for (int i = 0; i <= EncodingMemory.Capacity; i++)
            memory.Remember($"/x/{i}.txt", TextEncoding.Gb18030(true));
        Assert.Equal(EncodingMemory.Capacity, memory.Count);
        Assert.Null(memory.Get("/x/0.txt"));
        Assert.NotNull(memory.Get($"/x/{EncodingMemory.Capacity}.txt"));
    }

    [Fact]
    public void CorruptStore_IsIgnored()
    {
        using var dir = new TempDir();
        var store = dir.File("encodings.json");
        File.WriteAllText(store, "{ not json");
        Assert.Equal(0, new EncodingMemory(store).Count);
    }

    [Fact]
    public void ConfirmedEncoding_IsUsedOnNextLoad()
    {
        using var dir = new TempDir();
        var file = dir.File("short.txt");
        File.WriteAllBytes(file, Encoding.GetEncoding(950).GetBytes("中文")); // 太短，自动检测会猜成 GB18030
        Assert.NotEqual(950, TextFileIO.Load(file).Encoding.CodePage);

        var memory = new EncodingMemory(null);
        memory.Remember(file, TextEncoding.FromCodePage(950));
        var r = TextFileIO.Load(file, remembered: memory.Get(file));
        Assert.Equal("中文", r.Text);
        Assert.True(r.Encoding.IsCertain);
    }
}
