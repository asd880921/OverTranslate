using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeRegionStateTests
{
    private static readonly List<Rectangle> OneLine = [new Rectangle(10, 40, 300, 20)];

    [Fact]
    public void StillContentIsRecognisedOnceItHasSettled()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        Assert.False(state.Observe(frame.Capture));   // first sighting — give it a poll to settle
        Assert.True(state.Observe(frame.Capture));    // held still, read it
    }

    [Fact]
    public void MovingContentIsRecognisedEvenThoughItNeverSettles()
    {
        // Before any text is known there is nothing to watch but the whole region, so a video still
        // reports a change on every poll. It has to be read anyway, or a session over moving content
        // would never get started.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxUnsettledPolls,
                "a region over constantly changing content was never recognised");
        }

        Assert.Equal(RealtimeRegionState.MaxUnsettledPolls, polls);
    }

    [Fact]
    public void BackgroundMotionAroundUnchangedTextIsIgnored()
    {
        // The whole point of watching strips: a subtitle sitting still over a playing video must not
        // be re-recognised on every poll just because the picture behind it moved.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        for (int i = 0; i < RealtimeRegionState.FullRescanPolls - 1; i++)
        {
            frame.MoveBackground();
            Assert.False(state.Observe(frame.Capture));
        }
    }

    [Fact]
    public void ChangedTextIsRecognisedWithinTwoPolls()
    {
        // The latency the reader actually feels. Two polls, not the six a region without known text
        // is allowed.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        frame.ChangeText();
        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxTextUnsettledPolls,
                "a changed line of text took too long to be recognised");
        }
    }

    [Fact]
    public void TextAppearingOutsideTheWatchedStripsIsCaughtByTheFullRescan()
    {
        // Nothing short of recognition can tell new text from background motion, so this is the one
        // case that still costs a periodic pass.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        frame.MoveBackground();

        for (int i = 0; i < RealtimeRegionState.FullRescanPolls - 1; i++)
            Assert.False(state.Observe(frame.Capture));

        Assert.True(state.Observe(frame.Capture));
    }

    [Fact]
    public void ACompletelyStillRegionIsNeverRecognisedTwice()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        // The idle path: nothing moved anywhere, so not even the full rescan may trigger a pass.
        for (int i = 0; i < RealtimeRegionState.FullRescanPolls * 3; i++)
            Assert.False(state.Observe(frame.Capture));
    }

    [Fact]
    public void OneEmptyPassDoesNotClearTheOverlayOrForgetWhereTheTextWas()
    {
        // Recognition drops a line it had a moment ago often enough. Acting on the first empty pass
        // makes the translation blink out and come straight back.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        state.MarkRendered([], frame.Capture, "");

        Assert.False(state.ShouldClearOverlay);
        Assert.True(state.IsWatchingText);
    }

    [Fact]
    public void EmptyPassesInARowClearTheOverlayAndGoBackToWatchingTheWholeRegion()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        for (int i = 0; i < RealtimeRegionState.EmptyPassesBeforeClearing; i++)
            state.MarkRendered([], frame.Capture, "");

        Assert.True(state.ShouldClearOverlay);
        Assert.False(state.IsWatchingText);

        // Back on the whole-region path, so a background change is a change again.
        frame.MoveBackground();
        Assert.False(state.Observe(frame.Capture));
        Assert.True(state.Observe(frame.Capture));
    }

    [Fact]
    public void FindingTextAgainCancelsThePendingClear()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");
        state.MarkRendered([], frame.Capture, "");

        state.MarkRendered(OneLine, frame.Capture, "hello again");

        Assert.False(state.ShouldClearOverlay);
        Assert.True(state.IsWatchingText);
    }

    [Fact]
    public void ARegionHuntingForTextScansOftenEnoughToCatchAShortLine()
    {
        // A line that shows for a second and a half has to be caught inside its own lifetime. With a
        // 250ms poll this is the search rate over live content, so it has to stay well under that.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
        }

        Assert.True(polls <= 2, $"a region with no known text only scanned every {polls + 1} polls");
    }

    [Fact]
    public void TheSearchRateNeverSlowsDownHoweverLongTheRegionStaysEmpty()
    {
        // Easing off after a quiet spell would save real work, but the moment it eased off is the
        // moment a short line could slip through between scans — and the user cannot tell that from
        // the feature not working.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        for (int i = 0; i < 40; i++)
            state.MarkRendered([], frame.Capture, "");

        // Moved before the first poll: MarkRendered has just recorded the current picture, so a poll
        // taken against it would only report that nothing has happened yet.
        frame.MoveBackground();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
        }

        Assert.Equal(RealtimeRegionState.MaxUnsettledPolls, polls);
    }

    [Fact]
    public void RenderedTextIsWhatWasLastMarked()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        Assert.Equal("", state.RenderedText);

        state.MarkRendered(OneLine, frame.Capture, "hello");
        Assert.Equal("hello", state.RenderedText);
    }

    /// <summary>
    /// Stands in for a captured frame: one brightness for the whole region, another for the text
    /// strips, so a test can move the background and the text independently. Each step is well over
    /// the fingerprint's tolerance, so a move here is unambiguously a change.
    /// </summary>
    private sealed class FakeFrame
    {
        private byte _background = 20;
        private byte _text = 200;

        public void MoveBackground() => _background += 40;
        public void ChangeText() => _text -= 40;

        public FrameFingerprint Capture(IReadOnlyList<Rectangle>? bands)
        {
            var cells = new byte[100];
            Array.Fill(cells, bands is null ? _background : _text);
            return new FrameFingerprint(cells);
        }
    }
}
