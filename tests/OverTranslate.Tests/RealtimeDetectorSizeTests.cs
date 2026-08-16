using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

public class RealtimeDetectorSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void ASubtitleStripIsReadNearFullScale()
    {
        // Halving was the old answer and it read a fifth of the control corpus wrongly — words cut
        // in half, leading words dropped. See RealtimeDetectorSize for the sweep. #81
        var (primary, _) = RealtimeDetectorSize.For(1226, 196, RealtimeBlockMode.Subtitle);

        Assert.Equal(1056, primary); // 1226 * 0.85, rounded up onto the detector's grid
    }

    [Fact]
    public void AnInterfacePanelIsReadAtTheLargerFraction()
    {
        // The text is a small part of a panel's height, so halving takes it below what the detector
        // finds. Measured on this shape, 0.5 read 3 of 9 boxes and 0.68 read 7.
        var (primary, _) = RealtimeDetectorSize.For(1380, 750, RealtimeBlockMode.Panel);

        Assert.Equal(960, primary); // 1380 * 0.68
    }

    [Theory]
    // The same rectangle, read both ways. These are the shapes the removed StripAspectRatio rule
    // classified — a strip at 6.4 and a panel at 2.1 — and the point of issue #35 is that the shape
    // no longer decides anything: the user does, and either answer is available for either shape.
    [InlineData(1432, 224)]
    [InlineData(1380, 657)]
    public void TheModeDecidesTheScaleAndTheShapeNoLongerDoes(int width, int height)
    {
        var native = Math.Max(width, height);

        var strip = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Subtitle).Primary;
        var panel = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Panel).Primary;

        // Stated as the gap between the two answers rather than as a bound on each: what this test
        // is about is that the same rectangle gets two different sizes depending on what the user
        // says is in it, and that stays true however the two fractions are later retuned.
        Assert.NotEqual(strip, panel);
        Assert.Equal(RoundToStride(native * RealtimeDetectorSize.StripFraction), strip);
        Assert.Equal(RoundToStride(native * RealtimeDetectorSize.PanelFraction), panel);
    }

    private static int RoundToStride(double size) => Math.Max(320, ((int)size + 31) / 32 * 32);

    [Fact]
    public void TheUsersDraggingCannotChangeTheScaleAnyMore()
    {
        // What the user actually varies: the same subtitle framed tightly and framed loosely. Under
        // the aspect-ratio guess these landed on opposite sides of 4.0 and got two different scales
        // from the same content — the complaint issue #35 opens with.
        var tight = RealtimeDetectorSize.For(1432, 180, RealtimeBlockMode.Subtitle);   // ratio 8.0
        var loose = RealtimeDetectorSize.For(1432, 480, RealtimeBlockMode.Subtitle);   // ratio 3.0

        Assert.Equal(tight.Primary, loose.Primary);
    }

    [Fact]
    public void BothFractionsStayInsideTheRangeTheSweepSupports()
    {
        // This used to read 0.37–0.78, the window PP-OCRv5_mobile_det read those subtitle frames in
        // before collapsing above 0.84. That detector is gone and its collapse went with it — see
        // the note in RealtimeDetectorSize, and SubtitleFramesTheDetectorCollapsedAreReadAtTheChosenSize
        // below, which reads the very frames it collapsed on and is the guard that actually holds.
        //
        // What replaces it is the floor the control sweep measured: below 0.6 accuracy falls away
        // (0.5 read 78% of the corpus correctly, 0.4 read 69%), and native is worse than 0.85, so
        // neither fraction should sit outside that band. #81
        Assert.InRange(RealtimeDetectorSize.StripFraction, 0.60, 0.95);
        Assert.InRange(RealtimeDetectorSize.PanelFraction, 0.60, 0.95);
    }

    [Fact]
    public void TheOtherModesFractionIsTriedFirstThenNativeSize()
    {
        // The two fail on opposite content, so the fraction the mode turned down is the best next
        // answer when the mode was not the whole story. Native goes last: the only size that can
        // read text too small to survive any downscale.
        var (_, fallbacks) = RealtimeDetectorSize.For(1415, 169, RealtimeBlockMode.Subtitle);

        Assert.Equal([992, 1415], fallbacks); // 1415 * 0.68, then native
    }

    [Fact]
    public void ASizeThatWouldRepeatTheFirstAttemptIsNotTriedAgain()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(1226, 196, RealtimeBlockMode.Subtitle);

        Assert.DoesNotContain(primary, fallbacks);
    }

    [Fact]
    public void ASmallRegionIsReadAtNativeSizeWithNothingToFallBackTo()
    {
        // 60px subtitles do not fit in a region this size, so halving could only take small text
        // further out of range.
        var (primary, fallbacks) = RealtimeDetectorSize.For(400, 120, RealtimeBlockMode.Subtitle);

        Assert.Equal(400, primary);
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void TheLongestSideDecidesTheSize()
    {
        // The size comes from the long side whichever way round the block is — a tall, narrow panel
        // is measured on its height.
        var (primary, _) = RealtimeDetectorSize.For(300, 1000, RealtimeBlockMode.Panel);
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

        var (primary, _) = RealtimeDetectorSize.For(frame.Width, frame.Height, RealtimeBlockMode.Subtitle);
        var blocks = await engine.TryRecognizeAsync(frame, "EN", primary);

        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []).ToLowerInvariant();
        output.WriteLine($"{fixture} @ detect={primary} -> {text}");

        Assert.Contains(expected, text);
    }

    [Fact]
    public void ABlankRegionStopsPayingForTheNativeRetry()
    {
        // The expensive one goes; the other mode's fraction, which rescued 17 of the 23
        // blank-state rescues on record against native's 6, stays.
        var (_, fallbacks) = RealtimeDetectorSize.For(1699, 242, RealtimeBlockMode.Subtitle);
        var whileBlank = RealtimeDetectorSize.WhileNothingIsShown(fallbacks, 1699, 242);

        Assert.Contains(1699, fallbacks);
        Assert.DoesNotContain(1699, whileBlank);
        Assert.Equal(fallbacks.Count - 1, whileBlank.Count);
    }

    [Fact]
    public void ARegionTooSmallToDownscaleHasNothingToDrop()
    {
        // Below HalfScaleMinSide there are no fallbacks at all, so there is nothing here to save
        // and nothing to lose either.
        var (_, fallbacks) = RealtimeDetectorSize.For(400, 120, RealtimeBlockMode.Subtitle);

        Assert.Empty(RealtimeDetectorSize.WhileNothingIsShown(fallbacks, 400, 120));
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
