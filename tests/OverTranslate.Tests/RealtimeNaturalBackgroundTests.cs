using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeNaturalBackgroundTests
{
    [Fact]
    public void CreatePatch_ReplacesSourceRectButKeepsSurroundingPixels()
    {
        using var frame = new Bitmap(48, 24);
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
            frame.SetPixel(x, y, Color.FromArgb(255, 20 + y * 3, 30 + y * 2, 45 + y));

        var source = new System.Windows.Rect(14, 8, 20, 7);
        using (var g = Graphics.FromImage(frame))
            g.FillRectangle(Brushes.White, 14, 8, 20, 7);

        var patchBounds = new Rectangle(6, 3, 36, 17);
        using var patch = RealtimeNaturalBackground.CreatePatch(frame, patchBounds, [source]);

        Assert.NotNull(patch);
        Assert.Equal(frame.GetPixel(7, 4), patch!.GetPixel(1, 1));

        // The centre was solid white in the source. Natural repair must replace it with local
        // background rather than simply copying the source screenshot into the overlay.
        var repaired = patch.GetPixel(18, 9);
        Assert.True(repaired.R < 180);
        Assert.True(repaired.G < 180);
        Assert.True(repaired.B < 180);
    }

    [Fact]
    public void CreatePatch_CleansDetachedMarksAboveTightOcrBox()
    {
        using var frame = new Bitmap(100, 50);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 18, 78, 62));
            // Main glyph body is inside the OCR rectangle. The two small marks imitate Japanese
            // dakuten that OCR often leaves outside a tight line box.
            g.FillRectangle(Brushes.White, 25, 20, 50, 14);
            g.FillRectangle(Brushes.White, 34, 13, 3, 5);
            g.FillRectangle(Brushes.White, 40, 13, 3, 5);
        }

        var source = new System.Windows.Rect(25, 20, 50, 14);
        using var patch = RealtimeNaturalBackground.CreatePatch(
            frame, new Rectangle(10, 5, 80, 40), [source]);

        Assert.NotNull(patch);
        // Global (35,14) becomes local (25,9). It was white before repair and should now be close
        // to the green background rather than surviving as a bright fragment.
        var repairedMark = patch!.GetPixel(25, 9);
        Assert.True(repairedMark.R < 100);
        Assert.True(repairedMark.G < 140);
        Assert.True(repairedMark.B < 120);
    }

    [Fact]
    public void SampleTextColor_PrefersContrastingSourceGlyphColour()
    {
        using var frame = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 24, 28, 34));
            g.FillRectangle(Brushes.White, 20, 12, 40, 12);
        }

        var sampled = RealtimeNaturalBackground.SampleTextColor(
            frame,
            new System.Windows.Rect(20, 12, 40, 12),
            System.Windows.Media.Colors.Lime);

        Assert.True(sampled.R > 220);
        Assert.True(sampled.G > 220);
        Assert.True(sampled.B > 220);
    }
}
