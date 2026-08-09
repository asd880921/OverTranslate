namespace OverTranslate.Services.Realtime;

/// <summary>
/// Recognises the detector having collapsed: one box thrown across the whole watched block, out of
/// which the recogniser reads a character or two of nonsense.
/// </summary>
/// <remarks>
/// Both halves of that sentence are tested, and the second half is what makes the first one safe to
/// use. The block's height is the user's, so a rule that reads only the box's share of it hands the
/// user's dragging a say in whether correct readings survive: a block drawn tight around one line of
/// subtitle makes that line 100% of the block, and issue #35 measured 22 of 45 frames read as
/// nothing that way. Requiring the reading to be short as well costs nothing — the text is already
/// in hand by the time this runs — and the two populations do not overlap anywhere near the middle:
/// every collapse on record read "A", "M", "A" or "Yay!", and the sentence the height test alone
/// threw away was 39 characters of correct English.
///
/// What it is worth, from <c>OcrHarness --margin-sweep</c> over 39 real region dumps, scored as the
/// share of each frame's own best reading:
///
/// <code>
///   margin        height only   + short reading      frames reading nothing
///   0 (tight)        47.8%          75.3%              20/39  ->  9/39
///   0.15 lines       70.8%          77.6%              11/39  ->  8/39
///   0.50 lines       86.4%          85.2%               5/39      5/39
///   1.50 lines       97.2%          96.9%               1/39      1/39
/// </code>
///
/// The cliff at the tight end loses half its drop and the loose end does not move — the 0.3pp there
/// is the run-to-run noise of the same sweep. It is not flat yet, and what remains at 0 is issue
/// #35's second root cause, which no filter can fix: strokes clipped at the block's edge were never
/// captured.
///
/// Measured on a 1226x196 block whose real lines were 88 to 117 pixels tall. Four collapses in one
/// session returned boxes of 194, 253, 303 and 343 pixels — 99% to 175% of the block's own height —
/// holding "A", "M" and "A". They do real damage: a one-character reading looks nothing like the
/// subtitle on screen, so it counts as a new sentence and replaces a correct translation with a
/// giant A.
///
/// The height test is against the block the user drew, and nothing else. An earlier version learned
/// each region's usual line height and rejected anything far above it, which worked on a block holding
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

    /// <summary>
    /// Characters a block-spanning box may hold and still be a collapse rather than a line.
    /// </summary>
    /// <remarks>
    /// Every collapse ever recorded here read four characters or fewer — "A", "M", "A", "Yay!" —
    /// and the real sentences the height test was throwing away were 39 and 40. Ten sits in that
    /// gap rather than on either edge of it.
    ///
    /// The failure this leaves open is a collapse that reads long: a stretched crop that returns
    /// "Mainasm ar you lay" would now be kept. That is the cheaper failure by a wide margin. A
    /// collapse that gets through puts one bad line on screen for one pass, and the next pass
    /// replaces it; a real sentence rejected never reaches the screen at all, and it is rejected
    /// every pass it appears in, because what caused it is the shape of the block and the block
    /// does not change.
    ///
    /// Deliberately not <see cref="ShortReadingDetection.ShortTextLength"/>, which happens to be
    /// the same number. That one separates real short lines from scenery over any box; this
    /// separates collapses from sentences over a box that already spans the block. Two measurements
    /// that agree today, not one measurement used twice.
    /// </remarks>
    public const int MaxCollapsedReadingLength = 10;

    /// <param name="text">
    /// What the recogniser read out of the box. Null or empty counts as short: a box thrown across
    /// the whole block that produced nothing is the same event by the quietest route.
    /// </param>
    public static bool IsCollapsed(double boxHeight, double blockHeight, string? text) =>
        blockHeight > 0
        && boxHeight >= blockHeight * MaxShareOfBlockHeight
        && (text?.Trim().Length ?? 0) <= MaxCollapsedReadingLength;
}
