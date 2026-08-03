using System.Windows;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Tests;

// The bubble layout used to live inside OverlayWindow, reachable only by putting a window on a
// screen. It now computes plain data so both the live overlay and the batch image export can share
// it — and so the rules below can be pinned. The one that matters most is coverage: a bubble that
// does not fully cover the text it replaces leaves the original bleeding out from underneath.
public class OverlayBubbleLayoutTests
{
    private static OverlayLayoutContext Context(
        double dpi = 1.0, double canvasWidth = 1000, double canvasHeight = 1000,
        string source = "EN", string target = "ZH-HANT") =>
        new(DpiX: dpi,
            DpiY: dpi,
            OriginPhysX: 0,
            OriginPhysY: 0,
            OriginPhysWidth: canvasWidth * dpi,
            OriginPhysHeight: canvasHeight * dpi,
            SurfacePhysLeft: 0,
            SurfacePhysTop: 0,
            CanvasWidth: canvasWidth,
            CanvasHeight: canvasHeight,
            SourceLanguage: source,
            TargetLanguage: target);

    private static TranslatedBlock Block(
        double x, double y, double w, double h,
        string translated = "翻譯後的文字",
        string original = "original text",
        Color background = default,
        Color foreground = default) =>
        new(original, translated, new Rect(x, y, w, h), null, null, background, foreground);

    [Fact]
    public void Bubble_FullyCoversTheTextItReplaces()
    {
        var block = Block(100, 100, 200, 26);

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([block], Context()));

        Assert.True(bubble.Left <= 100, $"left {bubble.Left} starts after the source");
        Assert.True(bubble.Top <= 100, $"top {bubble.Top} starts below the source");
        Assert.True(bubble.Left + bubble.Width >= 300, $"right {bubble.Left + bubble.Width} stops short");
        Assert.True(bubble.Top + bubble.Height >= 126, $"bottom {bubble.Top + bubble.Height} stops short");
    }

    [Fact]
    public void BlocksWithNoTranslation_ProduceNoBubble()
    {
        var blocks = new List<TranslatedBlock>
        {
            Block(10, 10, 100, 20, translated: ""),
            Block(10, 50, 100, 20, translated: "   "),
            Block(10, 90, 100, 20, translated: "有內容"),
        };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(blocks, Context()));
        Assert.Equal("有內容", bubble.Text);
    }

    [Fact]
    public void SampledColors_AreCarriedThrough()
    {
        var background = Color.FromRgb(20, 40, 60);
        var foreground = Color.FromRgb(240, 230, 220);

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(
            [Block(10, 10, 120, 22, background: background, foreground: foreground)], Context()));

        Assert.Equal(background, bubble.Background);
        Assert.Equal(foreground, bubble.Foreground);
    }

    // A transparent sample means "we could not read a colour there" — white with a legible
    // contrasting foreground beats painting the bubble see-through.
    [Fact]
    public void UnsampledColors_FallBackToWhiteWithDarkText()
    {
        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(
            [Block(10, 10, 120, 22)], Context()));

        Assert.Equal(Colors.White, bubble.Background);
        Assert.Equal(Colors.Black, bubble.Foreground);
    }

    [Fact]
    public void DarkBackground_GetsLightTextWhenTextColourWasNotSampled()
    {
        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(
            [Block(10, 10, 120, 22, background: Color.FromRgb(10, 10, 10))], Context()));

        Assert.Equal(Colors.White, bubble.Foreground);
    }

    // Block bounds are physical pixels; the canvas works in DIPs. Getting this backwards puts every
    // bubble at twice its correct offset on a 200% display.
    [Fact]
    public void PhysicalBounds_AreConvertedToCanvasUnitsByDpi()
    {
        var block = Block(400, 200, 200, 40);

        var at100 = Assert.Single(OverlayBubbleLayout.Calculate([block], Context(dpi: 1.0)));
        var at200 = Assert.Single(OverlayBubbleLayout.Calculate([block], Context(dpi: 2.0)));

        Assert.True(at200.Left < at100.Left / 2 + 5 && at200.Left > at100.Left / 2 - 5,
            $"expected roughly half of {at100.Left}, got {at200.Left}");
    }

    [Fact]
    public void BubblesNeverEscapeTheCanvas()
    {
        var blocks = new List<TranslatedBlock>
        {
            Block(0, 0, 60, 20, translated: "很長的一段翻譯文字需要空間"),
            Block(940, 960, 200, 60, translated: "靠右下角的區塊"),
        };

        foreach (var bubble in OverlayBubbleLayout.Calculate(blocks, Context()))
        {
            Assert.True(bubble.Left >= 0, $"left {bubble.Left} is off-canvas");
            Assert.True(bubble.Top >= 0, $"top {bubble.Top} is off-canvas");
            Assert.True(bubble.Left + bubble.Width <= 1000, "bubble runs past the right edge");
            Assert.True(bubble.Top + bubble.Height <= 1000, "bubble runs past the bottom edge");
        }
    }

    // A source that OCR grouped into several lines is wrapped rather than squeezed onto one.
    [Fact]
    public void MultiLineSource_Wraps()
    {
        var block = new TranslatedBlock(
            "line one\nline two",
            "第一行的翻譯內容以及第二行的翻譯內容",
            new Rect(50, 50, 300, 60),
            [new Rect(50, 50, 300, 28), new Rect(50, 82, 300, 28)],
            null,
            default,
            default);

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([block], Context()));
        Assert.True(bubble.Wrap);
    }

    [Fact]
    public void ShortSingleLineSource_DoesNotWrap()
    {
        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(
            [Block(50, 50, 400, 24, translated: "短句")], Context()));

        Assert.False(bubble.Wrap);
    }

    // The batch export renders straight onto the source image: no DPI scaling and an origin at the
    // image's top-left. Same layout code, so this is the shape that has to keep working.
    [Fact]
    public void RegionOffset_PlacesBubblesRelativeToTheRegion()
    {
        var block = Block(10, 10, 100, 24);
        var context = Context() with { OriginPhysX = 500, OriginPhysY = 300 };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([block], context));

        Assert.True(bubble.Left >= 490 && bubble.Left <= 512, $"left {bubble.Left} ignored the region offset");
        Assert.True(bubble.Top >= 300 && bubble.Top <= 312, $"top {bubble.Top} ignored the region offset");
    }

    // The screen overlay relies on growing sideways to keep a translation on one line, and its
    // selection already bounds that growth. This default must not change.
    [Fact]
    public void WithoutACeiling_AShortLineIsAllowedToGrowSideways()
    {
        var block = Block(100, 400, 60, 24, translated: "一段明顯比原文寬的翻譯內容");

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([block], Context()));

        Assert.True(bubble.Width > 64 * 2.5,
            $"width {bubble.Width} suggests the unlimited default stopped expanding");
    }

    // Exporting a page has no selection edge, so an unbounded bubble stretches across the artwork.
    [Fact]
    public void WithACeiling_TheBubbleStopsExpandingAndWrapsInstead()
    {
        var block = Block(100, 400, 60, 24, translated: "一段明顯比原文寬的翻譯內容");
        var bounded = Context() with { MaxWidthFactor = 2.2 };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([block], bounded));

        Assert.True(bubble.Width <= 64 * 2.2 + 0.5, $"width {bubble.Width} broke the ceiling");
    }

    // ── Vertical (comic) layout ──────────────────────────────────────────────────────────────

    [Fact]
    public void HorizontalLayout_IsNotVertical()
    {
        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([Block(10, 10, 200, 24)], Context()));

        Assert.False(bubble.Vertical);
    }

    // The whole point of keeping the direction: the replacement needs no more room than the text it
    // covers, so it cannot spill onto the artwork around it.
    [Fact]
    public void VerticalLayout_StaysWithinTheSourceFootprint()
    {
        var column = Block(500, 200, 44, 260, translated: "關於我們別擔心");
        var vertical = Context() with { VerticalText = true };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([column], vertical));

        Assert.True(bubble.Vertical);
        Assert.True(bubble.Width <= 44 + 8, $"width {bubble.Width} grew past the column it replaces");
        Assert.True(bubble.Left <= 500 && bubble.Left + bubble.Width >= 544, "the source is left uncovered");
    }

    [Fact]
    public void VerticalCells_RunDownThenLeftwards()
    {
        var column = Block(500, 200, 40, 200, translated: "一二三四五六");
        var vertical = Context() with { VerticalText = true };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([column], vertical));
        var cells = OverlayBubbleLayout.VerticalCells(bubble).ToList();

        Assert.Equal(6, cells.Count);
        Assert.Equal('一', cells[0].Glyph);

        // Second character sits below the first, never beside it.
        Assert.True(cells[1].Cell.Y > cells[0].Cell.Y, "the column does not read downwards");
        Assert.Equal(cells[0].Cell.X, cells[1].Cell.X, 3);

        // Whichever character starts the next column must sit to the LEFT of the first one.
        var nextColumn = cells.FirstOrDefault(c => c.Cell.X < cells[0].Cell.X - 1);
        if (nextColumn.Cell.Width > 0)
            Assert.True(nextColumn.Cell.X < cells[0].Cell.X, "columns do not run right to left");
    }

    // A long translation must not silently lose its tail.
    [Fact]
    public void VerticalLayout_KeepsEveryCharacter()
    {
        const string translated = "這是一段相當長的翻譯內容需要很多空間才放得下";
        var column = Block(500, 200, 40, 160, translated: translated);
        var vertical = Context() with { VerticalText = true };

        var bubble = Assert.Single(OverlayBubbleLayout.Calculate([column], vertical));

        Assert.Equal(translated.Length, OverlayBubbleLayout.VerticalCells(bubble).Count());
    }

    // Set vertically, a glyph drawn as a horizontal stroke has to be turned or it reads as a
    // leftover from the horizontal layout — the ellipsis and the dash are the two that show up in
    // nearly every translated line.
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
        Assert.True(OverlayBubbleLayout.RotatesInVerticalText(glyph), $"'{glyph}' was left lying down");

    // Square glyphs are already the right way up; turning them would be the bug.
    [Theory]
    [InlineData('中')] [InlineData('あ')] [InlineData('ア')] [InlineData('한')]
    [InlineData('、')] [InlineData('。')] [InlineData('，')] [InlineData('！')] [InlineData('？')]
    [InlineData('：')] [InlineData('；')] [InlineData('・')]
    [InlineData('Ａ')] [InlineData('０')]   // full-width forms are made for vertical setting
    [InlineData('A')] [InlineData('0')]     // half-width runs would have to turn as a run, not per cell
    public void UprightGlyphs_AreLeftAlone(char glyph) =>
        Assert.False(OverlayBubbleLayout.RotatesInVerticalText(glyph), $"'{glyph}' was turned needlessly");

    [Fact]
    public void FontSize_StaysReadableForTinySourceText()
    {
        var bubble = Assert.Single(OverlayBubbleLayout.Calculate(
            [Block(10, 10, 200, 9, translated: "小字")], Context()));

        Assert.True(bubble.FontSize >= 10, $"font size {bubble.FontSize} is too small to read");
    }
}
