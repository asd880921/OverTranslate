namespace OverTranslate.Layout;

/// <summary>
/// How tall a screenshot-overlay bubble is drawn once its translation has been wrapped.
/// </summary>
/// <remarks>
/// Three things pull on the number and they disagree, which is why it is worth naming: the bubble
/// has to cover its own source, it should not reach over the block below, and it must hold the text
/// that was put in it. The bubble clips its contents, so the last of those is not a preference —
/// losing to it is the truncation of issue #73 arriving by a second route, without even a "…" to
/// show the reader that something went missing.
/// </remarks>
internal static class OverlayBubbleHeight
{
    /// <param name="sourceBorderHeight">The block's own height: what has to stay covered.</param>
    /// <param name="preferredHeight">
    /// What the bubble would be without a neighbour to consider — never below
    /// <paramref name="sourceBorderHeight"/>.
    /// </param>
    /// <param name="wrappedTextHeight">What the wrapped translation needs, padding included.</param>
    /// <param name="capHeight">
    /// Room before the block below, or null when nothing is under this one.
    /// </param>
    public static double ForWrapped(
        double sourceBorderHeight,
        double preferredHeight,
        double wrappedTextHeight,
        double? capHeight)
    {
        var wanted = Math.Max(preferredHeight, wrappedTextHeight);
        if (capHeight is not { } cap) return wanted;

        // Never below the source's own height. When the next block sits right under a multi-line
        // source the cap comes out slightly shorter than the source, and clamping to it left the
        // last source line uncovered, with the original text bleeding through. Covering one's own
        // source wins over not touching a neighbour — that neighbour draws its own opaque bubble
        // over the overlap anyway.
        //
        // And never below the text. Reaching that floor means no size the caller tried fitted the
        // cap, so the choice left is between a bubble that grows past its neighbour and a sentence
        // that stops early. The bubble is the one the user can see and act on.
        return Math.Max(wrappedTextHeight, Math.Min(wanted, Math.Max(cap, sourceBorderHeight)));
    }
}
