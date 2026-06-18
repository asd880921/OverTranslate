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
    public void MergesPerWordBoxesOnOneLineWhenBoxTopsVaryFromAscendersAndDescenders()
    {
        // Real geometry: a single line the detector split into per-word boxes whose tops vary
        // (32..37) with ascenders/descenders. A Y-primary sort interleaves them, so the words must
        // be re-merged by reading order into one line instead of separate translations.
        var blocks = new List<OcrTextBlock>
        {
            new("Send", new Rect(0, 32, 132, 59)),
            new("and", new Rect(1082, 34, 100, 59)),
            new("terminal", new Rect(1475, 34, 246, 57)),
            new("this", new Rect(150, 35, 134, 57)),
            new("the", new Rect(632, 35, 101, 56)),
            new("background", new Rect(753, 35, 309, 58)),
            new("free", new Rect(1203, 35, 130, 58)),
            new("the", new Rect(1353, 35, 101, 56)),
            new("session", new Rect(302, 36, 221, 54)),
            new("to", new Rect(539, 37, 72, 54)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
        Assert.Equal("Send this session to the background and free the terminal", grouped[0].Text);
    }

    [Fact]
    public void MergesHeadingWordBoxesThatOverlapHorizontallyFromUnclipExpansion()
    {
        // Real geometry from a large EN capture: the detector's unclip expansion enlarges the big
        // heading word-boxes until they overlap horizontally ("Translate" right edge 533 vs
        // "your website" left edge 515 → gap -18). They must still merge into one line instead of
        // being translated separately as "翻譯" + "你的 網站".
        var blocks = new List<OcrTextBlock>
        {
            new("Translate", new Rect(136, 68, 397, 99)),
            new("your website", new Rect(515, 71, 565, 102)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
        Assert.Equal("Translate your website", grouped[0].Text);
    }

    [Fact]
    public void MergesShortMidLineWordThatIsFullyNestedInTheLine()
    {
        // Real geometry from a captured line: the short word "to" (h=25) is shorter than its
        // neighbours (h=31) so heightRatio is only ~0.81, but it sits on the same baseline (fully
        // nested, vertical overlap ~1.0) and must not be dropped out of the middle of the line.
        var blocks = new List<OcrTextBlock>
        {
            new("session", new Rect(120, 8, 95, 31)),
            new("to", new Rect(215, 11, 30, 25)),
            new("the", new Rect(250, 8, 46, 31)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
        Assert.Equal("session to the", grouped[0].Text);
    }

    [Fact]
    public void KeepsTwoDistinctSameRowButtonsSeparate()
    {
        // Real geometry: two distinct buttons on the same row — "Download key-level usage report"
        // (h=32) and a shorter/taller, vertically offset "Create key" (h=38). heightRatio 0.84 and
        // vertical overlap 0.47 must keep them apart; over-merging them was a regression.
        var blocks = new List<OcrTextBlock>
        {
            new("Download key-level usage report", new Rect(1293, 321, 305, 32)),
            new("Create key", new Rect(1632, 298, 112, 38)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
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
