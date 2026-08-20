using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

public class QuickLookupLiftTests
{
    // A 1080p work area with the taskbar taken off it, and the popup's own 4px edge margin.
    private const int AreaTop = 0;
    private const int AreaBottom = 1040;
    private const int Edge = 4;

    private const int HeaderOnly = 120;   // the popup before a translation arrives
    private const int WithResult = 300;   // the same popup with the result panel open

    private static (int Top, int? RestingTop) Place(int currentTop, int? restingTop, int height)
        => QuickLookupLift.Place(currentTop, restingTop, height, AreaTop, AreaBottom, Edge);

    [Fact]
    public void WithRoomBelow_LeavesTheWindowAlone()
    {
        var (top, resting) = Place(currentTop: 300, restingTop: null, height: WithResult);

        Assert.Equal(300, top);
        Assert.Null(resting);
    }

    [Fact]
    public void ResultGrowingPastTheBottom_LiftsItJustInside()
    {
        // Dropped 900px down: the header fits, but 900 + 300 = 1200 runs 160px off a 1040px desktop.
        var (top, resting) = Place(currentTop: 900, restingTop: null, height: WithResult);

        Assert.Equal(AreaBottom - WithResult - Edge, top);   // 736 — the whole result is on screen
        Assert.Equal(900, resting);                          // and this is where it came from
    }

    [Fact]
    public void ResultClosing_PutsItBackWhereTheUserLeftIt()
    {
        var (lifted, resting) = Place(currentTop: 900, restingTop: null, height: WithResult);
        var (top, stillHeld) = Place(lifted, resting, HeaderOnly);

        Assert.Equal(900, top);
        Assert.Null(stillHeld);
    }

    [Fact]
    public void RepeatedTranslations_DoNotWalkTheWindowUpTheScreen()
    {
        // The bug this exists to prevent: judging the fit from where the window currently is means
        // a lifted popup always fits, so it never comes down and each result lifts it again.
        int home = 900;
        int? resting = null;
        int top = home;

        for (int i = 0; i < 5; i++)
        {
            (top, resting) = Place(top, resting, WithResult);   // a result opens
            Assert.Equal(AreaBottom - WithResult - Edge, top);

            (top, resting) = Place(top, resting, HeaderOnly);   // and closes again
            Assert.Equal(home, top);
            Assert.Null(resting);
        }
    }

    [Fact]
    public void AlreadyLiftedAndStillTooTall_HoldsTheSameRestingPlace()
    {
        // A second, longer translation while the first is still open: it lifts further, and what it
        // has to come back to is the user's position, not the height it was lifted to before.
        var (_, resting) = Place(currentTop: 900, restingTop: null, height: WithResult);
        var (top, stillHeld) = Place(736, resting, height: 500);

        Assert.Equal(AreaBottom - 500 - Edge, top);
        Assert.Equal(900, stillHeld);
    }

    [Fact]
    public void TallerThanTheWorkArea_PinsToTheTopRatherThanAbove()
    {
        // Math.Clamp throws if the bottom bound falls below the top one, and a window pushed above
        // the work area loses the box the user types in — the one part that must stay reachable.
        var (top, resting) = Place(currentTop: 200, restingTop: null, height: 2000);

        Assert.Equal(AreaTop + Edge, top);
        Assert.Equal(200, resting);
    }

    [Fact]
    public void SittingAboveTheWorkArea_ComesDownToTheEdge()
    {
        // A second monitor above this one puts negative tops in range; the popup still belongs on
        // the monitor it is being measured against.
        var (top, _) = Place(currentTop: -50, restingTop: null, height: HeaderOnly);

        Assert.Equal(AreaTop + Edge, top);
    }

    [Fact]
    public void DownwardMove_NeverCountsAsALift()
    {
        // Only being pushed up is a loan that has to be repaid. Clamping downwards is the window
        // arriving where it belongs, and remembering the position above it would drag it back there.
        var (top, resting) = Place(currentTop: -50, restingTop: null, height: HeaderOnly);

        Assert.True(top > -50);
        Assert.Null(resting);
    }

    [Fact]
    public void AWorkAreaNotStartingAtZero_IsRespectedAtBothEnds()
    {
        // A monitor to the right of the primary one, or one with the taskbar at the top.
        var (top, resting) = QuickLookupLift.Place(
            currentTop: 1900, restingTop: null, height: WithResult,
            areaTop: 1080, areaBottom: 2120, edge: Edge);

        Assert.Equal(2120 - WithResult - Edge, top);
        Assert.Equal(1900, resting);
    }
}
