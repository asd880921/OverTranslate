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
/// interface panel holds much smaller text and needs the opposite correction. See
/// <see cref="StripFraction"/>, <see cref="PanelFraction"/> and <see cref="StripAspectRatio"/>.
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
    /// Width-to-height ratio at or above which a block is treated as a subtitle strip.
    /// </summary>
    /// <remarks>
    /// One fraction cannot serve both shapes, and trying briefly made subtitles much worse: with
    /// 0.68 applied to everything, a session over a 1432x224 subtitle read nothing on a third of its
    /// passes and paid for two more inferences each time, while the game panels it was meant to help
    /// succeeded on 95–98% of theirs.
    ///
    /// The two shapes separate cleanly in the blocks users actually draw. From one afternoon's
    /// sessions:
    ///
    /// <code>
    ///   strips  1432x224  1549x203  1361x139  1856x152  1522x166   ratio 5.8 – 12.2
    ///   panels  1273x647  1395x636  1380x657  1295x696  1865x1050  ratio  1.8 –  2.9
    /// </code>
    ///
    /// Nothing lands between 2.9 and 5.8, so the threshold sits in the gap rather than on a measured
    /// edge. A ratio is the right test rather than an absolute height: it says the same thing about
    /// a block on a 1080p screen and a 4K one.
    /// </remarks>
    public const double StripAspectRatio = 4.0;

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
    /// which is why the shape rule above picks a starting point rather than one size trying to
    /// serve everything.
    ///
    /// The fallbacks are still earning their place under PP-OCRv6_det_tiny, on a smaller margin
    /// and for a different reason. Of 84 control frames the old detector read at the primary size,
    /// the new one reads 82 there and the other two only from 0.70 up — so those two are read by
    /// the first fallback rather than lost, at the price of one extra inference. What changed is
    /// how often that happens: on the frames a live session had to fall back for, the primary size
    /// now reads 9 of 15 rather than 4, which is the trigger rate coming down. Rarely used is what
    /// a fallback is supposed to be.
    ///
    /// The first fallback is whichever shape fraction the rule did not choose: the two fail on
    /// opposite content, so the rejected one is the best answer for the case where the rule was
    /// wrong. Native goes last as the only size that can read text too small to survive any
    /// downscale — it recovered nine lines in a single measured session.
    /// </remarks>
    public const double NativeFraction = 1.0;

    /// <param name="primary">Detector input size to read with first.</param>
    /// <param name="fallbacks">
    /// Sizes to try, in order, when a read finds nothing at all — stopping at the first that reads
    /// something. Empty is the signal because every way of being out of the detector's range
    /// produces it: text too large collapses into one unreadable box, text too small is never
    /// detected, and a scale the model simply dislikes returns nothing.
    /// </param>
    public static (int Primary, IReadOnlyList<int> Fallbacks) For(int width, int height)
    {
        var native = Math.Max(width, height);

        // ImgResize only ever downscales, so passing the longest side asks for the image as it is.
        if (native < HalfScaleMinSide)
            return (native, []);

        // Which shape this is decides where to start; the other shape's fraction is then the first
        // thing to try if that was wrong.
        var isStrip = height > 0 && (double)width / height >= StripAspectRatio;
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

    // The detector works on a grid; sizes off it are rounded up internally anyway, and rounding
    // here keeps the number in the log the same as the one the model actually used.
    private static int RoundToStride(int size) => Math.Max(320, (size + 31) / 32 * 32);
}
