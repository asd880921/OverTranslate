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
/// model was trained. So a region large enough to hold subtitle-sized text was first read at half
/// scale — see <see cref="PrimaryFraction"/> for why it no longer is.
///
/// None of this reaches the screenshot flow, which asks for no downscale at all and is right to:
/// its text is interface-sized, already inside the detector's range, and downscaling it would
/// destroy the detail that <c>ImgResize = 2048</c> exists to keep.
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
    /// Fraction of a large block's longest side to read it at first.
    /// </summary>
    /// <remarks>
    /// 0.68 rather than the 0.5 this shipped with. The fallback measurements below already said so —
    /// 0.68 read 8 of 9 frames the shipped pair had failed on, more than any other fraction — but it
    /// was only ever reached as a fallback, and a fallback only runs when the primary reads
    /// <em>nothing</em>. A primary that reads a little never escalates, so on any frame where 0.5
    /// found some of the text and missed the rest, the rest was silently lost.
    ///
    /// That case turns out to be the normal one outside subtitles. Measured on a 1380x750 grab of a
    /// game screen carrying a nine-box item tooltip:
    ///
    /// <code>
    ///   0.5  → 704   221ms   3 of 9 boxes
    ///   0.68 → 960   405ms   7 of 9 boxes
    ///   1.0  → 1380  724ms   9 of 9 boxes
    /// </code>
    ///
    /// 405ms is slower than a subtitle needs but far faster than a tooltip changes, and reading four
    /// more boxes is not a refinement — it is the difference between a translated card and a card
    /// with English still showing between the translated lines, which is what a partial read looks
    /// like on screen (nothing detected means nothing drawn over it either).
    ///
    /// Regions below <see cref="HalfScaleMinSide"/> are untouched: they are read at native size and
    /// this fraction never applies to them, so the subtitle case measured above is unchanged.
    /// </remarks>
    public const double PrimaryFraction = 0.68;

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
    /// happily. What it does say is that 0.68 recovers most of what is currently lost, so it goes
    /// first among the fallbacks. Native stays behind it rather than being dropped: it recovered
    /// nine lines in a single measured session, and it is the only size that can read text too
    /// small to survive any downscale at all.
    /// </remarks>
    /// <remarks>
    /// 0.5 is here rather than as the primary now that <see cref="PrimaryFraction"/> has taken that
    /// place. It stays in the list because the two are good at opposite things: 0.68 keeps small
    /// interface text detectable, 0.5 is what stops 60px subtitle glyphs collapsing into one box.
    /// Native last, as the only size that can read text too small to survive any downscale.
    /// </remarks>
    public static readonly double[] FallbackFractions = [0.5, 1.0];

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

        var primary = RoundToStride((int)(native * PrimaryFraction));
        var fallbacks = FallbackFractions
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
