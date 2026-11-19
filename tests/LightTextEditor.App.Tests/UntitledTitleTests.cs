using Avalonia.Headless.XUnit;
using LightTextEditor.App.ViewModels;
using LightTextEditor.Core.Text;

namespace LightTextEditor.App.Tests;

/// <summary>未命名文档的标题取第一行有内容的文字（只在可能影响它的改动后重新计算）。</summary>
public class UntitledTitleTests
{
    private static DocumentViewModel NewDoc() => DocumentViewModel.CreateNew(".txt", TextEncoding.Utf8(true), LineEnding.CrLf);

    [AvaloniaFact]
    public void Title_FollowsFirstNonEmptyLine_ThroughEdits()
    {
        var doc = NewDoc();
        var d = doc.Document;
        Assert.Equal("无标题", doc.Title);

        d.Insert(0, "\n\n# 购物清单\n- 牛奶\n");
        Assert.Equal("购物清单", doc.Title);

        d.Insert(d.TextLength, "- 面包\n");                 // 标题之后的改动
        Assert.Equal("购物清单", doc.Title);

        d.Insert(d.GetLineByNumber(3).EndOffset, "（周末）");  // 改标题行末尾
        Assert.Equal("购物清单（周末）", doc.Title);

        d.Insert(0, "待办\n");                               // 前面插入新的一行
        Assert.Equal("待办", doc.Title);

        var first = d.GetLineByNumber(1);
        d.Remove(first.Offset, first.TotalLength);           // 删掉标题行：下一行有内容的成为标题
        Assert.Equal("购物清单（周末）", doc.Title);

        d.Text = "   \n\t\n";
        Assert.Equal("无标题", doc.Title);
        d.Insert(d.TextLength, "后来写的");
        Assert.Equal("后来写的", doc.Title);
    }

    [AvaloniaFact]
    public void LongFirstLine_IsTruncated()
    {
        var doc = NewDoc();
        doc.Document.Text = new string('长', 1000) + "\n第二行";
        Assert.Equal(new string('长', 30) + "…", doc.Title);
    }
}
