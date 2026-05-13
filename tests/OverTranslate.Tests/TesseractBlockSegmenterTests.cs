using System.Windows;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class TesseractBlockSegmenterTests
{
    [Fact]
    public void BuildBlocks_MergesCjkClustersOnSameRowIntoLogicalLines()
    {
        var words = new List<OcrWord>
        {
            new("夢", new Rect(11, 80, 18, 34)),
            new("限", new Rect(35, 80, 18, 34)),
            new("大", new Rect(59, 80, 18, 34)),
            new("みゅーた", new Rect(83, 80, 86, 34)),
            new("47", new Rect(218, 80, 34, 34)),
            new("都", new Rect(256, 80, 18, 34)),
            new("道府県", new Rect(278, 80, 74, 34)),
            new("の", new Rect(356, 80, 18, 34)),
            new("旅", new Rect(378, 80, 18, 34)),
            new("「", new Rect(400, 80, 10, 34)),
            new("スー", new Rect(414, 80, 76, 34)),
            new("パー", new Rect(11, 120, 60, 20)),
            new("ポジション", new Rect(75, 120, 146, 20)),
            new("て", new Rect(225, 120, 18, 20)),
            new("スピンアップ", new Rect(247, 120, 143, 20)),
            new("編て", new Rect(394, 120, 31, 20)),
            new("城", new Rect(425, 120, 18, 20)),
            new("公演", new Rect(447, 120, 40, 20)),
            new("のセットリストプレイリストを公開!", new Rect(11, 152, 371, 33))
        };

        var blocks = TesseractBlockSegmenter.BuildBlocks(words, 511);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("夢限大みゅーた47都道府県の旅「スー", blocks[0].Text);
        Assert.Equal("パーポジションてスピンアップ編て城公演", blocks[1].Text);
        Assert.Equal("のセットリストプレイリストを公開!", blocks[2].Text);
    }

    [Fact]
    public void BuildBlocks_KeepsLargeHorizontalTitleGapsAsSeparateBlocks()
    {
        var words = new List<OcrWord>
        {
            new("MAIN", new Rect(0, 0, 46, 18)),
            new("TITLE", new Rect(54, 0, 52, 18)),
            new("SUB", new Rect(210, 0, 35, 18)),
            new("TITLE", new Rect(252, 0, 52, 18))
        };

        var blocks = TesseractBlockSegmenter.BuildBlocks(words, 304);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("MAIN TITLE", blocks[0].Text);
        Assert.Equal("SUB TITLE", blocks[1].Text);
    }

    [Fact]
    public void BuildBlocks_DoesNotMergeSeparateColumnsAcrossRows()
    {
        var words = new List<OcrWord>
        {
            new("LEFT", new Rect(0, 0, 40, 18)),
            new("COLUMN", new Rect(48, 0, 60, 18)),
            new("RIGHT", new Rect(220, 24, 44, 18)),
            new("COLUMN", new Rect(272, 24, 60, 18))
        };

        var blocks = TesseractBlockSegmenter.BuildBlocks(words, 332);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("LEFT COLUMN", blocks[0].Text);
        Assert.Equal("RIGHT COLUMN", blocks[1].Text);
    }

    [Fact]
    public void BuildBlocks_MergesTinyContinuationRowOnlyWhenItLooksLikeTailText()
    {
        var words = new List<OcrWord>
        {
            new("ションてスピンアップ編", new Rect(0, 0, 190, 20)),
            new("て", new Rect(8, 24, 16, 18)),
            new("」", new Rect(28, 24, 14, 18))
        };

        var blocks = TesseractBlockSegmenter.BuildBlocks(words, 240);

        Assert.Single(blocks);
        Assert.Equal("ションてスピンアップ編て」", blocks[0].Text);
    }
}
