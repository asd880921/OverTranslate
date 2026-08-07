using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

public class RealtimeDetectorSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void ARegionLargeEnoughForSubtitlesIsReadAtHalfScaleFirst()
    {
        var (primary, fallback) = RealtimeDetectorSize.For(1226, 196);

        Assert.Equal(613 / 32 * 32 + 32, primary); // 1226/2 rounded up onto the detector's grid
        Assert.Equal(1226, fallback);              // native, for text too small to survive halving
    }

    [Fact]
    public void ASmallRegionIsReadAtNativeSizeWithNothingToFallBackTo()
    {
        // 60px subtitles do not fit in a region this size, so halving could only take small text
        // further out of range.
        var (primary, fallback) = RealtimeDetectorSize.For(400, 120);

        Assert.Equal(400, primary);
        Assert.Null(fallback);
    }

    [Fact]
    public void TheLongestSideDecides()
    {
        var (primary, _) = RealtimeDetectorSize.For(300, 1000);
        Assert.Equal(512, primary); // 1000/2 rounded up onto the detector's grid
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
    [InlineData("subtitle-over-light-floor-1226x196.png")]
    [InlineData("subtitle-lost-entirely-1226x196.png")]
    public async Task TheSameFramesReadAtTheOldSizeStillFail(string fixture)
    {
        // Pins the bug itself rather than only the fix: if a future model or setting makes native
        // size work again, this fails and the halving can be reconsidered on evidence.
        using var engine = new OcrService();
        using var frame = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

        var blocks = await engine.TryRecognizeAsync(frame, "EN", 2048);
        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []);
        output.WriteLine($"{fixture} @ detect=2048 -> \"{text}\"");

        Assert.DoesNotContain("okay", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Minato-san", text, StringComparison.OrdinalIgnoreCase);
    }
}
