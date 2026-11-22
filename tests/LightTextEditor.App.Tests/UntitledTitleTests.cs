using Avalonia.Headless.XUnit;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Library;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

/// <summary>未保存过的新文档一直显示“无标题”，保存后显示文件名。</summary>
public class UntitledTitleTests
{
    private static DocumentViewModel NewDoc() => DocumentViewModel.CreateNew(".txt", TextEncoding.Utf8(true), LineEnding.CrLf);

    [AvaloniaFact]
    public void UntitledDocument_KeepsItsTitle_UntilSaved()
    {
        var doc = NewDoc();
        var d = doc.Document;
        Assert.Equal("无标题", doc.Title);

        // 输入、删除第一行：标题不变（标签宽度不变，标签栏不会跟着滚动）
        d.Insert(0, "# 购物清单\n- 牛奶\n");
        Assert.Equal("无标题", doc.Title);
        var first = d.GetLineByNumber(1);
        d.Remove(first.Offset, first.TotalLength);
        Assert.Equal("无标题", doc.Title);

        // 保存时的默认文件名仍然取第一行有内容的文字
        Assert.Equal("- 牛奶", FileNaming.SuggestBaseName(d.Text, DateTime.Now));

        var dir = Path.Combine(TestAppBuilder.Root, "untitled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        doc.SaveTo(Path.Combine(dir, "购物清单.txt"));
        Assert.Equal("购物清单.txt", doc.Title);
        d.Insert(0, "新的第一行\n");
        Assert.Equal("购物清单.txt", doc.Title);
    }
}
