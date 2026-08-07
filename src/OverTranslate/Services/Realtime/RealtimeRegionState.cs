namespace OverTranslate.Services.Realtime;

/// <summary>
/// Decides, poll by poll, whether one watched region is worth recognising again. Holding this apart
/// from the loop keeps the policy — the part with all the ways to be subtly wrong — testable without
/// a screen, a model or a network.
/// </summary>
/// <remarks>
/// The policy balances two failure modes that pull in opposite directions. Recognising every changed
/// frame burns the CPU on text that is still animating in and produces a flicker of half-read lines.
/// Waiting for the picture to hold still instead means never recognising anything over a video or a
/// game, where the background repaints continuously and two identical frames simply never arrive —
/// which is most of what this feature is for. So: prefer a settled frame, but give up waiting after
/// <see cref="MaxUnsettledPolls"/> and read the moving one.
/// </remarks>
internal sealed class RealtimeRegionState
{
    // Six polls at 250ms caps the wait at 1.5s, which is also roughly the minimum a subtitle has to
    // stay up to be readable — so on moving content the first read lands while the line is still up.
    public const int MaxUnsettledPolls = 6;

    private ulong _renderedSignature;
    private ulong _pendingSignature;
    private int _unsettledPolls;

    /// <summary>The source text currently on screen for this region.</summary>
    public string RenderedText { get; private set; } = "";

    /// <summary>
    /// Records a freshly captured frame and reports whether it should be recognised now.
    /// </summary>
    public bool Observe(ulong signature)
    {
        // Identical to what is already translated on screen — the idle path, and the one that has to
        // stay free: an untouched region costs a grab and a hash, and nothing else.
        if (signature == _renderedSignature)
        {
            _pendingSignature = signature;
            _unsettledPolls = 0;
            return false;
        }

        // Changed, and not yet the same twice running. Give it another poll to settle — but only up
        // to the cap, or moving content would be watched forever without ever being read.
        if (signature != _pendingSignature && _unsettledPolls < MaxUnsettledPolls)
        {
            _pendingSignature = signature;
            _unsettledPolls++;
            return false;
        }

        _pendingSignature = signature;
        _unsettledPolls = 0;
        return true;
    }

    /// <summary>
    /// Records what the region now shows, so an unchanged frame is skipped from here on.
    /// <paramref name="sourceText"/> is the recognised text, which the caller compares against
    /// <see cref="RenderedText"/> to decide whether a translation is needed at all.
    /// </summary>
    public void MarkRendered(ulong signature, string sourceText)
    {
        _renderedSignature = signature;
        RenderedText = sourceText;
    }
}
