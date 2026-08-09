namespace OverTranslate.Services.Realtime;

/// <summary>
/// Recognises the detector having collapsed: one box thrown across the whole watched block, out of
/// which the recogniser reads a character or two of nonsense.
/// </summary>
/// <remarks>
/// Measured on a 1226x196 block whose real lines were 88 to 117 pixels tall. Four collapses in one
/// session returned boxes of 194, 253, 303 and 343 pixels — 99% to 175% of the block's own height —
/// holding "A", "M" and "A". They do real damage: a one-character reading looks nothing like the
/// subtitle on screen, so it counts as a new sentence and replaces a correct translation with a
/// giant A.
///
/// The test is against the block the user drew, and nothing else. An earlier version learned each
/// region's usual line height and rejected anything far above it, which worked on a block holding
/// only subtitles and failed badly on anything else: over a game with a chat log in the corner, the
/// 13-pixel chat lines taught the region that text is 13 pixels tall, and every 55-pixel subtitle
/// after that was thrown away as a misdetection. Worse, it could not recover — a rejected line was
/// not learned from, so the yardstick stayed wrong for the rest of the session. Any measure built
/// from what a region has seen has that failure in it somewhere; the height of the block does not
/// change, cannot be poisoned, and needs nothing remembered.
/// </remarks>
internal static class CollapsedDetection
{
    /// <summary>
    /// Share of the watched block's height a single box may reach before it is a collapse rather
    /// than a line. A real line leaves room above and below it — the measured subtitle took 48% of
    /// its block — while a collapse spans the block or overruns it.
    /// </summary>
    /// <remarks>
    /// Was 0.9, and 0.9 started throwing real subtitles away when the detector changed. PP-OCRv6
    /// returns looser boxes than the PP-OCRv5 model this was measured against: in one session it
    /// put a 171px box around "Let's pay CiRcLE visit on the way home." in a 190px block — 90.0%,
    /// a complete and correct sentence, discarded.
    ///
    /// The two populations still separate, just not with the old margin. Every collapse ever
    /// measured overruns or all but fills the block — 194, 253, 303 and 343 pixels in a 196px
    /// block (99%, 129%, 155%, 175%), and a 214px box holding "Yay!" in a 191px block (112%) under
    /// the new detector. The real line sits at 90%. 0.95 is the gap between them, and it is a gap
    /// rather than a safety margin, so a detector that returns looser boxes still is the thing to
    /// re-measure this against rather than to nudge this for.
    /// </remarks>
    public const double MaxShareOfBlockHeight = 0.95;

    public static bool IsCollapsed(double boxHeight, double blockHeight) =>
        blockHeight > 0 && boxHeight >= blockHeight * MaxShareOfBlockHeight;
}
