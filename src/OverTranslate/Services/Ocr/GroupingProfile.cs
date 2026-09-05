namespace OverTranslate.Services.Ocr;

/// <summary>
/// The thresholds one grouping pass runs on. Decided by the capture mode, never by the source
/// language.
/// </summary>
/// <remarks>
/// <para>The grouper is handed this rather than a <see cref="CaptureLayoutMode"/> so that no rule
/// inside it ever asks which mode is on. It only ever knows what its limits are this time, which
/// keeps the product meaning of the modes at the seam where the user chose one.</para>
///
/// <para>Both fields sit on the set-solid path — the strictest geometry in the file, a pair already
/// through the leading and shared-edge tests. That is the boundary: a mode may relax what counts as
/// evidence for a pair that already looks set solid, and may not relax the geometry that decides
/// whether it does.</para>
///
/// <para>TWO FIELDS, ON PURPOSE. Every one added has to arrive with a measured case from the image
/// corpus behind it — a third was designed and then dropped again when the rule it would have
/// relaxed turned out never to fire on any image measured. A profile with a dozen knobs is the mode
/// quietly becoming a second source language, which is the thing this whole seam exists to prevent.
/// Nothing in the type stops a fourth being added; this is a review contract, and a locked
/// constructor would only produce a test-only factory to get around it.</para>
/// </remarks>
/// <param name="TightlySetMinTextSizeRatio">
/// How close in text size two set-solid lines have to be. Only consulted for a pair that is already
/// tightly set; everything else is judged on the ordinary size gate.
/// </param>
/// <param name="WaiveLengthTestWhenSetSolid">
/// Whether a set-solid pair may join even when the line above is too short to have plausibly
/// wrapped. Speech bubbles open on one or two words, which no length test can call a wrap.
/// </param>
internal sealed record GroupingProfile(
    double TightlySetMinTextSizeRatio,
    bool WaiveLengthTestWhenSetSolid)
{
    /// <summary>
    /// Interfaces: game UI, menus, multi-column panels. The conservative half of the pair, and what
    /// every capture was grouped on before modes existed.
    /// </summary>
    /// <remarks>
    /// This was called Standard while it was the default. It is neither standard nor the default in
    /// v2 — the material people actually point this at is prose — but the figures have not moved,
    /// which is what makes the swap provable: an Interface run reproduces every corpus output saved
    /// under the old name byte for byte.
    /// </remarks>
    public static GroupingProfile Interface { get; } = new(
        TightlySetMinTextSizeRatio: OrdinaryMinTextSizeRatio,
        WaiveLengthTestWhenSetSolid: false);

    /// <summary>
    /// The default: articles, comics, dialogue, and most of what anyone frames. Relaxed only where a
    /// pair has already passed the set-solid geometry.
    /// </summary>
    /// <remarks>
    /// <para>Waives the length test, and only that. A speech bubble opens on one or two words —
    /// "WHY ARE", "HOW", "THE" — and no length test can call those a wrap, because as a statement
    /// about text in general it is right: a line that short did not run out of room. What makes it
    /// wrong here is the container. Inside a bubble the line ends where the bubble does, so a short
    /// opening line is the normal shape of speech rather than evidence that nothing follows.</para>
    ///
    /// <para>The user is the one who knows a bubble is a bubble, which is why this hangs off their
    /// declaration rather than off anything measured. It stays on the strictest path in the file —
    /// the pair must already be set solid, sharing an edge to within a third of a line height — so
    /// what is waived is one piece of evidence, never the geometry.</para>
    ///
    /// <para>Halving the length test for everyone was measured as the alternative and rejected: it
    /// buys the same eight comic pairs and charges ten wrong joins to game panels for them, which
    /// is precisely the cost a declared mode exists to avoid. See
    /// <c>OcrTextBlockGrouper.IsSetSolidUnder</c>.</para>
    ///
    /// <para>The size ratio is lowered as well, and 0.80 is where the measurement put it. Hand
    /// lettering is uneven — the same sentence's lines come back 0.83 to 0.88 of each other across
    /// the ten comic pages — so the ordinary figure refuses speech that is plainly one size. Going
    /// further was tried: 0.70 joins the last hand-marked group (a two-line bubble whose lines read
    /// 0.71) and takes the corpus from 23 groups to the hand-marked 22, but it also adds nineteen
    /// wrong joins across the other seven image sets when this mode is pointed at them — a
    /// breadcrumb over its page title, a stat row over the row below it, three pairs of identical
    /// level labels. One right answer for nineteen wrong ones is the wrong trade, so the last group
    /// stays apart and 23 is the number this corpus reaches.</para>
    /// </remarks>
    public static GroupingProfile General { get; } = new(
        TightlySetMinTextSizeRatio: 0.80,
        WaiveLengthTestWhenSetSolid: true);

    /// <summary>
    /// The live-screen path's own profile. It is not <see cref="Interface"/>, it merely holds the
    /// same figures today.
    /// </summary>
    /// <remarks>
    /// Realtime has no <see cref="CaptureLayoutMode"/>: there is no toolbar in front of a running
    /// video and nobody to answer for a frame that arrives every quarter second. It gets its own
    /// named profile so that the day it is tuned for speech versus interface, the screenshot side
    /// is not tuned with it by accident — which is what sharing <see cref="Interface"/> would set
    /// up, silently, the first time either number moved.
    /// </remarks>
    public static GroupingProfile Realtime { get; } = new(
        TightlySetMinTextSizeRatio: OrdinaryMinTextSizeRatio,
        WaiveLengthTestWhenSetSolid: false);

    /// <summary>The thresholds for a capture the user has declared the kind of.</summary>
    /// <remarks>
    /// The only place a mode becomes numbers. Anything else reading the mode to pick a threshold is
    /// a second copy of this table waiting to disagree with it.
    /// </remarks>
    public static GroupingProfile For(CaptureLayoutMode mode) => mode switch
    {
        CaptureLayoutMode.Interface => Interface,

        // General, and any mode a later release adds that this build does not know. The default
        // catches the unknown one deliberately: a settings file naming a mode this build cannot
        // read has already fallen back to General by the time it reaches here, and answering the
        // same way twice is cheaper than a second table that can disagree with the first.
        _ => General,
    };

    /// <summary>
    /// The size ratio every pair is held to, set solid or not. Taken from the grouper rather than
    /// repeated, so "the ordinary figure" cannot drift away from the figure itself.
    /// </summary>
    private const double OrdinaryMinTextSizeRatio = OcrTextBlockGrouper.MinTextSizeRatio;
}
