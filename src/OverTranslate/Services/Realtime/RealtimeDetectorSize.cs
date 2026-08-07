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
/// model was trained. So a region large enough to hold subtitle-sized text is read at half scale.
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

    /// <param name="primary">Detector input size to read with first.</param>
    /// <param name="fallback">
    /// Size to read with when the first attempt finds nothing at all, or null when there is no
    /// second size worth trying. Empty is the signal because both ways of being out of range
    /// produce it: text too large collapses into one unreadable box, text too small is never
    /// detected.
    /// </param>
    public static (int Primary, int? Fallback) For(int width, int height)
    {
        var native = Math.Max(width, height);

        // ImgResize only ever downscales, so passing the longest side asks for the image as it is.
        return native >= HalfScaleMinSide
            ? (RoundToStride(native / 2), native)
            : (native, null);
    }

    // The detector works on a grid; sizes off it are rounded up internally anyway, and rounding
    // here keeps the number in the log the same as the one the model actually used.
    private static int RoundToStride(int size) => Math.Max(320, (size + 31) / 32 * 32);
}
