using System.Drawing;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class DoubtfulBlocksTests
{
    private static OcrTextBlock Line(double confidence, double top = 100, double height = 40) =>
        new("text", new Rect(50, top, 400, height), Confidence: confidence);

    [Fact]
    public void OnlyLinesUnderTheFloorAreReRead()
    {
        // A correct Korean line in the sample scored 0.82, so the floor has to sit above it while
        // staying under the 0.94-1.00 band that everything well-read lands in.
        var blocks = new List<OcrTextBlock> { Line(0.99), Line(0.84), Line(0.85), Line(0.62) };

        var selected = DoubtfulBlocks.Select(blocks);

        Assert.Equal([3, 1], selected); // 0.85 is not under the floor; 0.62 goes first
    }

    [Fact]
    public void TheWorstLinesAreReReadFirstSoTheCapSpendsItselfWell()
    {
        var blocks = Enumerable.Range(0, 20).Select(i => Line(0.5 + i * 0.01)).ToList();

        var selected = DoubtfulBlocks.Select(blocks);

        Assert.Equal(DoubtfulBlocks.MaxRereads, selected.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], selected);
    }

    [Fact]
    public void LinesWithNoScoreAreLeftAlone()
    {
        // No score reported is not the same as a bad one, and re-reading every unscored line would
        // spend the whole budget on lines nothing is known about.
        var blocks = new List<OcrTextBlock> { new("text", new Rect(0, 0, 100, 20)) };

        Assert.Empty(DoubtfulBlocks.Select(blocks));
    }

    [Fact]
    public void ACleanCaptureCostsNothing()
    {
        var blocks = new List<OcrTextBlock> { Line(0.95), Line(0.99), Line(1.0) };

        Assert.Empty(DoubtfulBlocks.Select(blocks));
    }

    [Fact]
    public void TheCropCarriesAMarginTheDetectorCanFindAnEdgeIn()
    {
        var crop = DoubtfulBlocks.CropAround(new Rect(50, 100, 400, 40), 1000, 500);

        Assert.Equal(Rectangle.FromLTRB(34, 84, 466, 156), crop); // 40 * 0.4 = 16 each way
    }

    [Fact]
    public void ACropIsKeptInsideTheCapture()
    {
        var crop = DoubtfulBlocks.CropAround(new Rect(0, 0, 200, 40), 150, 30);

        Assert.Equal(new Rectangle(0, 0, 150, 30), crop);
    }

    [Fact]
    public void ALineOutsideTheCaptureCropsToNothing()
    {
        var crop = DoubtfulBlocks.CropAround(new Rect(500, 500, 100, 20), 200, 200);

        Assert.True(crop.Width <= 0 || crop.Height <= 0);
    }

    [Fact]
    public void WhatTheCropFindsInTheMarginIsNotMistakenForTheLine()
    {
        // The line sits at y 100-140 of the capture; the crop starts at y 84. Something found in
        // the crop's first few rows is the line above, caught by the margin.
        var original = new Rect(50, 100, 400, 40);
        var crop = DoubtfulBlocks.CropAround(original, 1000, 500);

        Assert.True(DoubtfulBlocks.IsSameLine(original, new Rect(10, 16, 400, 40), crop));
        Assert.False(DoubtfulBlocks.IsSameLine(original, new Rect(10, 0, 400, 8), crop));
    }
}
