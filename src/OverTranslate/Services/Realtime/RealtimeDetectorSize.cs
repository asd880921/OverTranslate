namespace OverTranslate.Services.Realtime;

/// <summary>
/// What size to hand the text detector when reading a watched region.
/// </summary>
/// <remarks>
/// The detector has a size of text it is good at, and subtitles are well outside it. Measured on two
/// captured frames of a 1226x196 subtitle strip, sweeping the detector's input size in steps of 64:
///
/// <list type="bullet">
/// <item>at 0.37–0.78 of native, both frames read correctly every time;</item>
/// <item>at 0.84 and above, both collapsed — the detector returned one box spanning the whole strip
/// (measured at 201 and 256 pixels tall against a real line height of 88–117), and recognising that
/// stretched crop produced either nothing at all or "Mainasm ar you lay" for "Marina-san, are you
/// okay?".</item>
/// </list>
///
/// The glyphs in those frames are around 60px tall; halving puts them near 30px, which is where the
/// model was trained. That is why a subtitle strip is read at half scale — but only a strip: an
/// interface panel holds much smaller text and needs the opposite correction. Which of the two a
/// block is comes from <see cref="RealtimeBlockMode"/>, i.e. from the user; see
/// <see cref="StripFraction"/> and <see cref="PanelFraction"/>.
///
/// None of this reaches the screenshot flow, which asks for no downscale at all and is right to:
/// its text is interface-sized, already inside the detector's range, and downscaling it would
/// destroy the detail that <c>ImgResize = 2048</c> exists to keep.
///
/// EVERYTHING ABOVE WAS MEASURED ON PP-OCRv5_mobile_det, WHICH IS NO LONGER THE DETECTOR. The
/// collapse it describes was real, and the reason for it — a size the model dislikes returns one
/// box across the whole strip — was the diagnosis issue #22 spent three rounds confirming. What
/// changed is the model: swept over 15 frames a live session had failed to read, PP-OCRv5 read 8
/// at 0.40, 4 at 0.50, 6 at 0.55 and 8 at 0.60, while PP-OCRv6_det_tiny reads 9 across that whole
/// band and 9 at native. There is no dead band left to steer around.
///
/// The fractions below did not change with it, and the reason they are still here changed
/// completely: they are now a cost decision, not an avoidance. A strip read at 0.50 and at native
/// reads the same 9 of those 15 frames, for 89ms against 186ms — so halving buys the same answer
/// for half the work. That is why the numbers stayed put; it is not evidence that they are still
/// optimal, and a sweep on the control group (frames the primary size already read) is what would
/// move them.
///
/// THE DETECTOR CHANGED AGAIN in #71, to PP-OCRv6_det_small, and the fractions still did not move.
/// That sweep is the control-group one this comment asked for: over 137 real region dumps the new
/// detector reads 8.6% more characters at 0.50 than the old one did, and 0.65 to 0.70 reads more
/// still — 1122 characters against 1025 — for 78% more time. So halving remains a cost decision and
/// the same one, now made against a sample that includes the frames the primary size already read.
/// </remarks>
internal static class RealtimeDetectorSize
{
    /// <summary>
    /// Below this, halving would take the text out of range from the other side, so the region is
    /// read at native size and only retried at half if that finds nothing. A region this small
    /// cannot hold 60px subtitles and much text besides.
    /// </summary>
    public const int HalfScaleMinSide = 800;

    /// <summary>
    /// Fraction to read a wide, short block at — a subtitle strip.
    /// </summary>
    /// <remarks>
    /// Text in a strip fills most of its height, so the glyphs are large: around 60px in the frames
    /// swept above, which halving puts near the 30px the model was trained on. Everything in this
    /// type's remarks about collapse comes from this shape.
    /// </remarks>
    public const double StripFraction = 0.5;

    /// <summary>
    /// Fraction to read a chunkier block at — an interface panel, a tooltip, a dialogue box.
    /// </summary>
    /// <remarks>
    /// The opposite problem. Text is a small part of a panel's height, so its glyphs are already
    /// near interface size and halving takes them below what the detector can find. Measured on a
    /// 1380x750 grab of a game screen carrying a nine-box item tooltip:
    ///
    /// <code>
    ///   0.5  → 704   221ms   3 of 9 boxes
    ///   0.68 → 960   405ms   7 of 9 boxes
    ///   1.0  → 1380  724ms   9 of 9 boxes
    /// </code>
    ///
    /// Reading four more boxes is not a refinement — a box that is never detected is never drawn
    /// over either, so a partial read shows on screen as a card with the original language still
    /// visible between the translated lines.
    ///
    /// 0.68 is also what the fallback sweep already favoured: it read 8 of the 9 frames the shipped
    /// pair had given up on, more than any other fraction. It was only ever reached as a fallback,
    /// and a fallback runs only when the primary reads <em>nothing</em> — so a primary that read a
    /// little never escalated and the rest was silently lost.
    /// </remarks>
    public const double PanelFraction = 0.68;

    /// <summary>
    /// Fractions of the block to fall back to, in the order to try them, when the first size reads
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The detector does not respond to scale smoothly, so there is no one size to get right.
    /// Sweeping one frame that reliably failed: 0.45 read it perfectly, 0.52 read nothing, 0.59
    /// read a fragment, 0.68 read it perfectly, 1.0 collapsed. Neighbouring sizes land on opposite
    /// sides of working.
    ///
    /// Which fractions, from 280 frames the loop had given up on, 9 of which held readable text:
    ///
    /// <code>
    ///   0.68 read 8 of 9      0.45 read 5      1.00 read 3
    ///   0.35 read 7 of 9      0.52 read 3
    /// </code>
    ///
    /// Those frames were kept precisely because the shipped pair failed on them, so the sample
    /// cannot say 0.68 is a better first choice than 0.52 — it never saw the frames 0.52 reads
    /// happily. What it does say is that neighbouring fractions land on opposite sides of working,
    /// which is why the mode above picks a starting point rather than one size trying to serve
    /// everything.
    ///
    /// The fallbacks were still earning their place under PP-OCRv6_det_tiny, on a smaller margin
    /// and for a different reason. Of 84 control frames the old detector read at the primary size,
    /// the new one reads 82 there and the other two only from 0.70 up — so those two are read by
    /// the first fallback rather than lost, at the price of one extra inference. What changed is
    /// how often that happens: on the frames a live session had to fall back for, the primary size
    /// now reads 9 of 15 rather than 4, which is the trigger rate coming down. Rarely used is what
    /// a fallback is supposed to be.
    ///
    /// Under det_small (#71) the trigger rate should be lower again — it reads more than tiny at
    /// every size — but that was not measured directly, because a fallback only fires on a frame the
    /// primary size read nothing in and #71 swept sizes rather than replaying sessions. Worth
    /// watching in a live session rather than assuming.
    ///
    /// The first fallback is whichever mode's fraction was not chosen: the two fail on opposite
    /// content, so the one the mode turned down is the best answer for the case where the mode was
    /// not the whole story — a subtitle block that also caught a line of interface text, say. Native
    /// goes last as the only size that can read text too small to survive any downscale — it
    /// recovered nine lines in a single measured session.
    /// </remarks>
    public const double NativeFraction = 1.0;

    /// <param name="mode">
    /// What the user says the block holds. This used to be guessed from the block's width-to-height
    /// ratio, which is a fact about how the user dragged rather than about what they are reading —
    /// see <see cref="RealtimeBlockMode"/> for what that cost. Issue #35 replaced the guess with
    /// the question.
    /// </param>
    /// <param name="primary">Detector input size to read with first.</param>
    /// <param name="fallbacks">
    /// Sizes to try, in order, when a read finds nothing at all — stopping at the first that reads
    /// something. Empty is the signal because every way of being out of the detector's range
    /// produces it: text too large collapses into one unreadable box, text too small is never
    /// detected, and a scale the model simply dislikes returns nothing.
    /// </param>
    public static (int Primary, IReadOnlyList<int> Fallbacks) For(
        int width, int height, RealtimeBlockMode mode)
    {
        var native = Math.Max(width, height);

        // ImgResize only ever downscales, so passing the longest side asks for the image as it is.
        if (native < HalfScaleMinSide)
            return (native, []);

        // The mode decides where to start; the other mode's fraction is then the first thing to try
        // if that start read nothing.
        var isStrip = mode == RealtimeBlockMode.Subtitle;
        var chosen = isStrip ? StripFraction : PanelFraction;
        var other = isStrip ? PanelFraction : StripFraction;

        var primary = RoundToStride((int)(native * chosen));
        var fallbacks = new[] { other, NativeFraction }
            .Select(fraction => fraction >= 1.0 ? native : RoundToStride((int)(native * fraction)))
            .Where(size => size != primary)
            .Distinct()
            .ToList();

        return (primary, fallbacks);
    }

    /// <summary>
    /// The fallbacks worth paying for while the region is known to be showing nothing at all.
    /// </summary>
    /// <remarks>
    /// Native size is the most expensive inference of the three and the least productive one here.
    /// Sorted by the state the region was in, the 163 rescues across four days of logs fall out as:
    ///
    /// <code>
    ///   the pass before had text     121   74%
    ///   the overlay still showed one  19   12%
    ///   the region had been cleared   23   14%
    /// </code>
    ///
    /// That last 14% is a new line arriving after a quiet stretch — the one nobody can afford to
    /// miss — so the fallbacks are not dropped there. But within it, the other mode's fraction
    /// rescued 17 of the 23 and native only 6, while native costs 190–220ms against the
    /// primary's ~90ms. Dropping it while the region is blank saves about a sixth of a subtitle
    /// session's whole recognition time for about 3.7% of its rescues.
    ///
    /// Only while blank. A region that has text on it, or had text a moment ago, keeps every size:
    /// that is where 86% of the rescues happen and where a missed line is a line the reader was in
    /// the middle of following.
    /// </remarks>
    public static IReadOnlyList<int> WhileNothingIsShown(
        IReadOnlyList<int> fallbacks, int width, int height)
    {
        var native = Math.Max(width, height);
        return [.. fallbacks.Where(size => size != native)];
    }

    // The detector works on a grid; sizes off it are rounded up internally anyway, and rounding
    // here keeps the number in the log the same as the one the model actually used.
    private static int RoundToStride(int size) => Math.Max(320, (size + 31) / 32 * 32);
}
