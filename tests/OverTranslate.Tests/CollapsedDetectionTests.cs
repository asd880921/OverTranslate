using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real line here means a subtitle never appears at all, which is exactly what the
/// earlier learned-height version of this did over a game with a chat log in it.
/// </summary>
public class CollapsedDetectionTests
{
    // The measured block: 196 tall, real lines 88-117, collapses at 194, 253, 303 and 343.
    private const double Block = 196;

    [Theory]
    [InlineData(194)]
    [InlineData(253)]
    [InlineData(303)]
    [InlineData(343)]
    public void ABoxThrownAcrossTheWholeBlockIsACollapse(double height)
    {
        Assert.True(CollapsedDetection.IsCollapsed(height, Block));
    }

    [Theory]
    [InlineData(88)]
    [InlineData(95)]
    [InlineData(117)]
    [InlineData(150)]
    public void RealLinesLeaveRoomAboveAndBelowThem(double height)
    {
        Assert.False(CollapsedDetection.IsCollapsed(height, Block));
    }

    [Theory]
    // The case that broke the version this replaced: one block holding a game's 13px chat log and
    // its 55-78px subtitles. Every size in it has to survive, because the block is what decides now
    // and the block has not changed.
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(55)]
    [InlineData(78)]
    public void TextOfEverySizeInAMixedBlockSurvives(double height)
    {
        Assert.False(CollapsedDetection.IsCollapsed(height, 400));
    }

    [Fact]
    public void ABlockOfNoHeightIsNotJudged()
    {
        Assert.False(CollapsedDetection.IsCollapsed(200, 0));
    }
}
