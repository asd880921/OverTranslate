using OverTranslate.Services;

namespace OverTranslate.Layout;

/// <summary>
/// Whether a colour sampled for the last overlay still describes the block now at that index.
/// </summary>
/// <remarks>
/// <para>Bubble colours are read out of the picture underneath each box, and a second translation
/// of the same selection re-uses them rather than reading the crop again. What made that safe was
/// an assumption that no longer holds: that the block at index <c>i</c> is the block that was at
/// index <c>i</c> last time. Switching capture mode regroups the reading, so the count and the
/// boxes both move; so does redrawing the selection, and so does the detector on its own.</para>
///
/// <para>Comparing the mode, or the number of blocks, would catch the loud cases and miss the
/// quiet one — same mode, same count, different members — which is the one that ends with a bubble
/// wearing the colours of whatever used to sit at its index and nothing on screen to say so. Each
/// block is therefore asked to identify itself, and a block that cannot is re-sampled: one pass
/// over a crop, against the OCR and the translation that have just run.</para>
/// </remarks>
internal static class SampledColorReuse
{
    /// <summary>
    /// How far a box may have moved and still be the same box, in source pixels. The detector does
    /// not return identical rectangles for identical text; exact equality would re-sample the whole
    /// overlay every time, which is correct but throws the optimisation away entirely.
    /// </summary>
    private const double SameBoxTolerance = 2.0;

    public static bool CanReuse(
        IReadOnlyList<TranslatedBlock> previous,
        int index,
        TranslatedBlock candidate,
        bool verticalText,
        bool previousVerticalText)
    {
        // The vertical pipeline lays out different boxes over the same picture, so nothing sampled
        // under one orientation says anything about the other.
        if (verticalText != previousVerticalText) return false;

        // Shorter than this run's list whenever the last attempt failed before it had colours.
        if (index < 0 || index >= previous.Count) return false;

        var earlier = previous[index];
        return string.Equals(candidate.OriginalText, earlier.OriginalText, StringComparison.Ordinal) &&
               IsNearlyTheSameBox(candidate.Bounds, earlier.Bounds);
    }

    private static bool IsNearlyTheSameBox(System.Windows.Rect a, System.Windows.Rect b) =>
        Math.Abs(a.X - b.X)           <= SameBoxTolerance &&
        Math.Abs(a.Y - b.Y)           <= SameBoxTolerance &&
        Math.Abs(a.Width - b.Width)   <= SameBoxTolerance &&
        Math.Abs(a.Height - b.Height) <= SameBoxTolerance;
}
