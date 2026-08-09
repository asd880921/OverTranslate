using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

public class RealtimeDetectorSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void ASubtitleStripIsReadAtHalfScale()
    {
        // Wide and short: the text fills the height, so the glyphs are large and halving lands them
        // near the size the model was trained on.
        var (primary, _) = RealtimeDetectorSize.For(1226, 196);   // ratio 6.3

        Assert.Equal(640, primary); // 1226 * 0.5, rounded up onto the detector's grid
    }

    [Fact]
    public void AnInterfacePanelIsReadAtTheLargerFraction()
    {
        // Chunky: the text is a small part of the height, so halving takes it below what the
        // detector finds. Measured on this shape, 0.5 read 3 of 9 boxes and 0.68 read 7.
        var (primary, _) = RealtimeDetectorSize.For(1380, 750);   // ratio 1.8

        Assert.Equal(960, primary); // 1380 * 0.68
    }

    [Theory]
    [InlineData(1432, 224)]   // the strips from a measured session, ratio 5.8 and up
    [InlineData(1549, 203)]
    [InlineData(1856, 152)]
    [InlineData(1361, 139)]
    public void TheShapesUsersActuallyDrawAreClassifiedAsStrips(int width, int height)
    {
        var (primary, _) = RealtimeDetectorSize.For(width, height);

        Assert.Equal(RealtimeDetectorSize.For(width, height).Primary, primary);
        Assert.True(primary <= width * 0.55, $"{width}x{height} should be read at strip scale");
    }

    [Theory]
    [InlineData(1273, 647)]   // the panels from the same session, ratio 2.9 and below
    [InlineData(1395, 636)]
    [InlineData(1865, 1050)]
    public void TheShapesUsersActuallyDrawAreClassifiedAsPanels(int width, int height)
    {
        var (primary, _) = RealtimeDetectorSize.For(width, height);
        var native = Math.Max(width, height);

        Assert.True(primary >= native * 0.6, $"{width}x{height} should be read at panel scale");
    }

    [Fact]
    public void BothFractionsStayInsideTheRangeSubtitlesSurvive()
    {
        // The original sweep found 0.37–0.78 of native read both subtitle frames every time, and
        // 0.84 and above collapsed them into one unreadable box. Either fraction can end up on a
        // strip — the panel one as a fallback — so both have to stay inside that window. This fails
        // the moment someone nudges one towards the collapse.
        Assert.InRange(RealtimeDetectorSize.StripFraction, 0.37, 0.78);
        Assert.InRange(RealtimeDetectorSize.PanelFraction, 0.37, 0.78);
    }

    [Fact]
    public void TheRejectedShapeFractionIsTriedFirstThenNativeSize()
    {
        // The two fail on opposite content, so when the shape rule was wrong the fraction it turned
        // down is the best next answer. Native goes last: the only size that can read text too small
        // to survive any downscale.
        var (_, fallbacks) = RealtimeDetectorSize.For(1415, 169);  // a strip, so primary is 0.5

        Assert.Equal([992, 1415], fallbacks); // 1415 * 0.68, then native
    }

    [Fact]
    public void ASizeThatWouldRepeatTheFirstAttemptIsNotTriedAgain()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(1226, 196);

        Assert.DoesNotContain(primary, fallbacks);
    }

    [Fact]
    public void ASmallRegionIsReadAtNativeSizeWithNothingToFallBackTo()
    {
        // 60px subtitles do not fit in a region this size, so halving could only take small text
        // further out of range.
        var (primary, fallbacks) = RealtimeDetectorSize.For(400, 120);

        Assert.Equal(400, primary);
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void TheLongestSideDecidesTheSize()
    {
        // Tall and narrow is not a strip, so it reads at panel scale — and the size comes from the
        // long side whichever way round the block is.
        var (primary, _) = RealtimeDetectorSize.For(300, 1000);
        Assert.Equal(704, primary); // 1000 * 0.68, rounded up onto the detector's grid
    }

    [Theory]
    // Two frames captured from a live session, both of which the realtime loop read as empty and
    // cleared the overlay for. The text in each is large, white, outlined and unmistakable; the
    // detector collapsed it into a single box spanning the whole strip. See RealtimeDetectorSize
    // for the sweep these came from.
    [InlineData("subtitle-over-light-floor-1226x196.png", "marina-san, are you okay?")]
    [InlineData("subtitle-lost-entirely-1226x196.png", "minato-san")]
    public async Task SubtitleFramesTheDetectorCollapsedAreReadAtTheChosenSize(string fixture, string expected)
    {
        using var engine = new OcrService();
        using var frame = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

        var (primary, _) = RealtimeDetectorSize.For(frame.Width, frame.Height);
        var blocks = await engine.TryRecognizeAsync(frame, "EN", primary);

        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []).ToLowerInvariant();
        output.WriteLine($"{fixture} @ detect={primary} -> {text}");

        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("subtitle-over-light-floor-1226x196.png", "okay")]
    [InlineData("subtitle-lost-entirely-1226x196.png", "minato-san")]
    public async Task TheSameFramesAlsoReadAtTheScreenshotSizeNow(string fixture, string expected)
    {
        // These two frames used to come back empty here, and the test that came before this one
        // pinned that failure on purpose — so that swapping the detector would trip it rather than
        // let the halving outlive its reason. PP-OCRv6_det_tiny tripped it: both frames now read
        // correctly at the size the screenshot flow uses, which closes the hole issue #22 recorded
        // in that flow (it has no fallback size to fall back to, so a subtitle framed there was
        // simply lost).
        //
        // Halving is still what a strip is read at, and now for a plain reason rather than a
        // detector quirk: swept over the same 15 frames, the new detector reads 9 at 0.50 and 9 at
        // native, for 89ms against 186ms. Same result, half the cost. If a later measurement shows
        // native reading materially more, this test is where the evidence for changing that starts.
        using var engine = new OcrService();
        using var frame = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

        var blocks = await engine.TryRecognizeAsync(frame, "EN", 2048);
        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []).ToLowerInvariant();
        output.WriteLine($"{fixture} @ detect=2048 -> \"{text}\"");

        Assert.Contains(expected, text);
    }
}
