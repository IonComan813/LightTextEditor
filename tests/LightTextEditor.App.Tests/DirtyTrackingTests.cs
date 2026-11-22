using Avalonia.Headless.XUnit;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

/// <summary>“未保存”标记按文本内容判断（长度 + XxHash3），不看撤销记录。</summary>
public class DirtyTrackingTests
{
    private static (DocumentViewModel Doc, string Path) Open(string text)
    {
        var dir = Path.Combine(TestAppBuilder.Root, "dirty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, text);
        return (DocumentViewModel.FromFile(path, TextFileIO.Load(path, LineEnding.Lf)), path);
    }

    [AvaloniaFact]
    public void TypingThenDeleting_IsNotDirty()
    {
        var (doc, _) = Open("你好世界");
        var d = doc.Document;
        d.Insert(2, "看不到");
        Assert.True(doc.IsDirty);
        d.Remove(4, 1); // 一个字一个字删掉（不是撤销）
        d.Remove(3, 1);
        Assert.True(doc.IsDirty);
        d.Remove(2, 1);
        Assert.Equal("你好世界", d.Text);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public void SameLengthDifferentText_IsDirty()
    {
        var (doc, _) = Open("abc\ndef");
        doc.Document.Replace(0, 1, "x");
        Assert.True(doc.IsDirty);
        doc.Document.Replace(0, 1, "a");
        Assert.False(doc.IsDirty);
        // 整篇换成同样长度的另一段文字，再换回来（一次改动，如“全部替换”）
        doc.Document.Replace(0, doc.Document.TextLength, "abd\ndef");
        Assert.True(doc.IsDirty);
        doc.Document.Replace(0, doc.Document.TextLength, "abc\ndef");
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public void AfterSaving_ComparesWithTheSavedText()
    {
        var (doc, path) = Open("abc");
        doc.Document.Insert(3, "d");
        doc.SaveTo(path);
        Assert.False(doc.IsDirty);

        doc.Document.Insert(4, "e");
        doc.Document.Remove(4, 1);
        Assert.False(doc.IsDirty);
        // 改回保存之前的内容：和磁盘上的不同了
        doc.Document.Remove(3, 1);
        Assert.True(doc.IsDirty);
        // 撤销回到保存时的状态
        doc.Document.UndoStack.Undo();
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public void ChangesOutsideTheText_StayDirty()
    {
        var (doc, _) = Open("abc");
        doc.ChangeEncoding(TextEncoding.Utf8(withBom: true));
        doc.Document.Insert(0, "x");
        doc.Document.Remove(0, 1);
        Assert.True(doc.IsDirty); // 编码改了，文本改回去也还是未保存
    }

    [AvaloniaFact]
    public void NewDocument_TypingThenDeleting_IsNotDirty()
    {
        var doc = DocumentViewModel.CreateNew(".txt", TextEncoding.Utf8(false), LineEnding.Lf);
        doc.Document.Insert(0, "ab");
        Assert.True(doc.IsDirty);
        doc.Document.Remove(0, 2);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public void Fingerprint_ReadsLongDocumentsInChunks()
    {
        var text = string.Concat(Enumerable.Range(0, 20000).Select(i => $"第{i}行\n"));
        var a = new AvaloniaEdit.Document.TextDocument(text);
        var b = new AvaloniaEdit.Document.TextDocument(text);
        Assert.Equal(TextFingerprint.Of(a), TextFingerprint.Of(b));
        b.Replace(text.Length - 3, 1, "x"); // 最后一块里改一个字
        Assert.NotEqual(TextFingerprint.Of(a), TextFingerprint.Of(b));
        Assert.Equal(TextFingerprint.Of(a).Length, TextFingerprint.Of(b).Length);
    }
}
