using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeRegionStateTests
{
    [Fact]
    public void StillContentIsRecognisedOnceItHasSettled()
    {
        var state = new RealtimeRegionState();

        Assert.False(state.Observe(100));   // first sighting — give it a poll to settle
        Assert.True(state.Observe(100));    // held still, read it
    }

    [Fact]
    public void MovingContentIsRecognisedEvenThoughItNeverSettles()
    {
        // The regression this exists for. A video, a game, or anything with an animated background
        // produces a different frame on every single poll, so a rule that waits for two identical
        // frames recognises nothing at all — which is exactly the content this feature is for.
        var state = new RealtimeRegionState();

        ulong signature = 1;
        int polls = 0;
        while (!state.Observe(signature++))
        {
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxUnsettledPolls,
                "a region over constantly changing content was never recognised");
        }

        Assert.Equal(RealtimeRegionState.MaxUnsettledPolls, polls);
    }

    [Fact]
    public void AnUnchangedRegionIsNeverRecognisedTwice()
    {
        var state = new RealtimeRegionState();
        state.Observe(100);
        Assert.True(state.Observe(100));
        state.MarkRendered(100, "hello");

        // The idle path: nothing on screen moved, so nothing may reach the recogniser again.
        for (int i = 0; i < 20; i++)
            Assert.False(state.Observe(100));
    }

    [Fact]
    public void ContentThatChangesAndSettlesAgainIsRecognisedAgain()
    {
        var state = new RealtimeRegionState();
        state.Observe(100);
        state.Observe(100);
        state.MarkRendered(100, "hello");

        Assert.False(state.Observe(200));   // new frame, let it settle
        Assert.True(state.Observe(200));
    }

    [Fact]
    public void SettlingBeforeTheCapDoesNotWaitForIt()
    {
        // Text that animates in for a few frames and then holds must be read the moment it holds,
        // not at the cap — otherwise every subtitle would pay the full 1.5s.
        var state = new RealtimeRegionState();

        Assert.False(state.Observe(1));
        Assert.False(state.Observe(2));
        Assert.False(state.Observe(3));
        Assert.True(state.Observe(3));
    }

    [Fact]
    public void RenderedTextIsWhatWasLastMarked()
    {
        var state = new RealtimeRegionState();
        Assert.Equal("", state.RenderedText);

        state.MarkRendered(100, "hello");
        Assert.Equal("hello", state.RenderedText);
    }
}
