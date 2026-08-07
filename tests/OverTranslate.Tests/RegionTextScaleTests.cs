using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real line here means a subtitle never appears, so the bar is deliberately set where
/// only a misdetection can reach it.
/// </summary>
public class RegionTextScaleTests
{
    private static RegionTextScale Watched(double lineHeight = 86, int lines = 8)
    {
        var scale = new RegionTextScale();
        for (int i = 0; i < lines; i++)
            scale.Observe(lineHeight, glyphCount: 20);
        return scale;
    }

    [Fact]
    public void NothingIsRejectedBeforeTheRegionIsKnown()
    {
        var scale = new RegionTextScale();
        scale.Observe(86, glyphCount: 20);

        // One line seen. A 343px box is certainly wrong, but refusing text this early would break
        // a region whose very first reading happens to be its only one.
        Assert.Null(scale.UsualHeight);
        Assert.False(scale.IsOversized(343));
    }

    [Theory]
    // The four misdetections measured in one session, against real lines of 86px.
    [InlineData(194)]
    [InlineData(253)]
    [InlineData(303)]
    [InlineData(343)]
    public void ABoxFarTallerThanTheRegionsLinesIsRejected(double height)
    {
        Assert.True(Watched().IsOversized(height));
    }

    [Theory]
    // A real "YA" arrives in a box the same height as everything else, and lines vary a little.
    [InlineData(95)]
    [InlineData(86)]
    [InlineData(120)]
    [InlineData(150)]
    public void RealLinesAreKeptEvenWhenShortOrSlightlyTaller(double height)
    {
        Assert.False(Watched().IsOversized(height));
    }

    [Fact]
    public void ShortBoxesAreNeverLearnedFrom()
    {
        // The boxes under suspicion are exactly the short ones; letting them into the sample would
        // teach the region that giant boxes are normal and admit the next one.
        var scale = new RegionTextScale();
        for (int i = 0; i < 8; i++)
            scale.Observe(343, glyphCount: 1);

        Assert.Null(scale.UsualHeight);
    }

    [Fact]
    public void OneMisdetectionAmongRealLinesDoesNotRaiseTheBar()
    {
        // Median, not mean: a single 343 that arrived in a long-enough line must not drag the
        // yardstick up far enough to admit the next one.
        var scale = Watched();
        scale.Observe(343, glyphCount: 20);

        Assert.Equal(86, scale.UsualHeight);
        Assert.True(scale.IsOversized(253));
    }

    [Fact]
    public void TheRegionFollowsContentThatGenuinelyChangesSize()
    {
        // A player moving from subtitles to a larger menu: once the window has turned over, the
        // new size is the normal one and is no longer rejected.
        var scale = Watched();
        for (int i = 0; i < RegionTextScale.Window; i++)
            scale.Observe(200, glyphCount: 20);

        Assert.Equal(200, scale.UsualHeight);
        Assert.False(scale.IsOversized(253));
    }
}
