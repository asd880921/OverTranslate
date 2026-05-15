using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class OcrTextBlockGrouperTests
{
    [Fact]
    public void GroupsAdjacentAlignedLinesIntoSingleBlock()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家", new Rect(10, 40, 110, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
        Assert.Equal(2, grouped[0].Lines.Count);
        Assert.Equal("寧夏夜市美食攻略 食尚玩家", grouped[0].Text);
    }

    [Fact]
    public void KeepsDifferentColumnsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("LEFT", new Rect(10, 10, 100, 20)),
            new("RIGHT", new Rect(220, 38, 110, 20)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsDifferentFontSizesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 180, 28)),
            new("summary", new Rect(10, 48, 180, 16)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsTitleAndBodySeparateWhenHeightsAreCloseButDistinct()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 220, 28)),
            new("body text", new Rect(10, 44, 220, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void MergesAdjacentFragmentsOnTheSameLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏", new Rect(10, 10, 42, 24)),
            new("夜市", new Rect(58, 10, 42, 24)),
            new("攻略", new Rect(106, 10, 42, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
        Assert.Equal("寧夏夜市攻略", grouped[0].Text);
    }

    [Fact]
    public void KeepsSeparatePhrasesOnTheSameLineWhenGapIsLarge()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("左側", new Rect(10, 10, 42, 24)),
            new("右側", new Rect(140, 10, 42, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void GroupsWrappedSentenceWhenOpeningQuoteContinuesOnNextLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Poppin'Party×Roselia 合同ライブ「DREAMS", new Rect(10, 10, 330, 24)),
            new("GO ON」のセットリストプレイリストを公開！", new Rect(10, 40, 290, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
    }

    [Fact]
    public void KeepsAlignedIndependentLinesSeparateWhenWidthsAreSimilar()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家最新整理", new Rect(10, 40, 210, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsNextSentenceSeparateAfterSentenceTerminator()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("第一句已經結束。", new Rect(10, 10, 220, 24)),
            new("第二句重新開始", new Rect(10, 40, 110, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }
}
