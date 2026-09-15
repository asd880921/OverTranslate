using System.Drawing;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureBackgroundColorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sample_UsesParagraphInteriorInsteadOfDifferentSurroundingSurface(bool vertical)
    {
        using var frame = new Bitmap(160, 160);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.DarkBlue);
            g.FillRectangle(Brushes.Beige, 30, 30, 100, 100);
            if (vertical)
            {
                g.FillRectangle(Brushes.Black, 30, 30, 20, 100);
                g.FillRectangle(Brushes.Black, 110, 30, 20, 100);
            }
            else
            {
                g.FillRectangle(Brushes.Black, 30, 30, 100, 20);
                g.FillRectangle(Brushes.Black, 30, 110, 100, 20);
            }
        }
        var block = new TranslatedBlock("source", "譯文", new(30, 30, 100, 100),
            vertical ? [new(30, 30, 20, 100), new(110, 30, 20, 100)]
                     : [new(30, 30, 100, 20), new(30, 110, 100, 20)]);
        var background = CaptureBackgroundColor.Sample(frame, block, vertical);
        Assert.Equal(System.Windows.Media.Color.FromRgb(245, 245, 220), background);
        var colors = SourceTextColorSampler.ForCaptureOverlay(frame, block.Bounds, background);
        Assert.Equal(background, colors.Background);
        Assert.True(OverlayTextColor.ContrastRatio(colors.Text, colors.Background) >= OverlayTextColor.MinimumContrast);
    }

    [Fact]
    public void Sample_DeclinesSingleLineAndOverlappingRows()
    {
        using var frame = new Bitmap(100, 100);
        var single = new TranslatedBlock("a", "字", new(10, 10, 50, 20));
        Assert.Null(CaptureBackgroundColor.Sample(frame, single, false));
        var overlap = single with { SourceLineBounds = [new(10, 10, 50, 20), new(10, 15, 50, 20)] };
        Assert.Null(CaptureBackgroundColor.Sample(frame, overlap, false));
    }

    [Fact]
    public void Sample_DeclinesMixedSurfacesInGap()
    {
        using var frame = new Bitmap(100, 100);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 50, 0, 50, 100);
        }
        var block = new TranslatedBlock("a", "字", new(10, 10, 80, 80),
            [new(10, 10, 80, 20), new(10, 70, 80, 20)]);
        Assert.Null(CaptureBackgroundColor.Sample(frame, block, false));
    }

    [Fact]
    public void Sample_ClipsPartiallyOffscreenParagraph()
    {
        using var frame = new Bitmap(100, 100);
        using (var g = Graphics.FromImage(frame)) g.Clear(Color.Beige);
        var block = new TranslatedBlock("a", "字", new(-20, -10, 100, 100),
            [new(-20, -10, 100, 20), new(-20, 60, 100, 20)]);
        Assert.Equal(System.Windows.Media.Color.FromRgb(245, 245, 220), CaptureBackgroundColor.Sample(frame, block, false));
    }
}
