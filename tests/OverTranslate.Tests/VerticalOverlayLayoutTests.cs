using System.Windows;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class VerticalOverlayLayoutTests
{
    [Fact]
    public void IsVerticalSource_RecognizesMappedVerticalBlock()
    {
        var block = new TranslatedBlock(
            "日本語",
            "日文",
            new Rect(250, 20, 20, 100),
            [new Rect(252, 22, 16, 96)],
            SourceGlyphHeight: 20);

        Assert.True(VerticalOverlayLayout.IsVerticalSource(block));
    }

    [Fact]
    public void IsVerticalSource_DoesNotMistakeTallHorizontalParagraphForVerticalText()
    {
        var block = new TranslatedBlock(
            "a narrow paragraph",
            "狹窄段落",
            new Rect(20, 20, 40, 100),
            [new Rect(20, 20, 40, 20), new Rect(20, 50, 40, 20)],
            SourceGlyphHeight: 18);

        Assert.False(VerticalOverlayLayout.IsVerticalSource(block));
    }

    [Fact]
    public void Calculate_UsesSourceGlyphSizeInsteadOfShrinkingToNarrowHorizontalWidth()
    {
        var layout = VerticalOverlayLayout.Calculate(Input(
            text: "日本語",
            width: 20,
            height: 120,
            glyphSize: 20));

        Assert.Equal(20, layout.CellSize);
        Assert.Equal(18.4, layout.FontSize, precision: 3);
        Assert.True(layout.FontSize > 2.5 * 7, $"font size {layout.FontSize} still looks horizontally shrunk");
    }

    [Fact]
    public void Cells_RunDownThenContinueInTheColumnToTheLeft()
    {
        var layout = VerticalOverlayLayout.Calculate(Input(
            text: "一二三四五六",
            width: 44,
            height: 60,
            glyphSize: 20));

        var cells = VerticalOverlayLayout.Cells(layout).ToList();

        Assert.Equal(6, cells.Count);
        Assert.Equal(cells[0].Cell.X, cells[1].Cell.X, precision: 3);
        Assert.True(cells[1].Cell.Y > cells[0].Cell.Y);
        Assert.True(cells[3].Cell.X < cells[0].Cell.X);
    }

    [Fact]
    public void Calculate_RemovesSourceWhitespaceAndKeepsEveryTranslatedCharacter()
    {
        const string translated = "這 是一段\r\n相當長的翻譯內容需要很多空間才放得下";
        var layout = VerticalOverlayLayout.Calculate(Input(
            text: translated,
            width: 40,
            height: 80,
            glyphSize: 20));

        var expected = new string(translated.Where(character => !char.IsWhiteSpace(character)).ToArray());
        var cells = VerticalOverlayLayout.Cells(layout).ToList();

        Assert.Equal(expected, layout.Text);
        Assert.Equal(expected.Length, cells.Count);
        Assert.Equal(expected, string.Concat(cells.Select(cell => cell.Glyph)));
    }

    [Theory]
    [InlineData('…')] [InlineData('⋯')] [InlineData('‥')]
    [InlineData('—')] [InlineData('–')] [InlineData('―')] [InlineData('─')] [InlineData('━')]
    [InlineData('-')] [InlineData('－')] [InlineData('〜')] [InlineData('～')]
    [InlineData('ー')] [InlineData('ｰ')] [InlineData('＿')] [InlineData('＝')]
    [InlineData('「')] [InlineData('」')] [InlineData('『')] [InlineData('』')]
    [InlineData('（')] [InlineData('）')] [InlineData('【')] [InlineData('】')]
    [InlineData('《')] [InlineData('》')] [InlineData('〈')] [InlineData('〉')]
    [InlineData('［')] [InlineData('］')] [InlineData('｛')] [InlineData('｝')]
    [InlineData('(')] [InlineData(')')] [InlineData('[')] [InlineData(']')]
    public void SidewaysGlyphs_AreTurned(char glyph) =>
        Assert.True(VerticalOverlayLayout.RotatesInVerticalText(glyph));

    [Theory]
    [InlineData('中')] [InlineData('あ')] [InlineData('ア')] [InlineData('한')]
    [InlineData('、')] [InlineData('。')] [InlineData('，')] [InlineData('！')] [InlineData('？')]
    [InlineData('：')] [InlineData('；')] [InlineData('・')]
    [InlineData('Ａ')] [InlineData('０')] [InlineData('A')] [InlineData('0')]
    public void UprightGlyphs_AreLeftAlone(char glyph) =>
        Assert.False(VerticalOverlayLayout.RotatesInVerticalText(glyph));

    private static VerticalOverlayInput Input(
        string text,
        double width,
        double height,
        double glyphSize) => new(
            text,
            SourceLeft: 100,
            SourceTop: 50,
            SourceWidth: width,
            SourceHeight: height,
            SourceGlyphSize: glyphSize,
            CanvasWidth: 800,
            CanvasHeight: 600);
}
