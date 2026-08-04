namespace OverTranslate.Layout;

/// <summary>
/// Turns the height of the recognised source text into the overlay font size.
/// </summary>
/// <remarks>
/// Small source text needs proportionally more size than large text: at 12px the translated CJK
/// glyphs are far denser than the Latin original they replace, so matching the source height 1:1
/// renders them unreadable, while at 20px the same boost would look oversized. The ratios below
/// encode that, and they are the ones this overlay has always used.
///
/// What changed is how a block picks between them. This used to be a hard switch at
/// <c>height &lt;= 14</c> — under it, ×1.18 with a 14.5 floor (plus a further ×1.08 for Latin
/// sources rendered as CJK); over it, ×1.06 with a 13 floor. Two blocks a fraction of a pixel
/// apart therefore rendered ~18% apart, and OCR box heights are not stable to a fraction of a
/// pixel: measured heights for ordinary UI text cluster at 12–16, right on the boundary, and any
/// change to how the detector is fed moves them by ~0.5px. That is enough to tip a block across
/// and visibly resize its text without the underlying capture having changed at all.
///
/// Interpolating across a 12–16 band instead keeps both endpoints exactly as before — a 12px block
/// still gets the full small-text treatment, a 16px block still gets none — but makes everything
/// between them continuous, so a 0.5px shift in the measured height moves the font by ~0.5px
/// rather than by a fifth of its size.
/// </remarks>
public static class SourceFontScale
{
    // Below this the source counts as fully "small", above it as fully "large"; in between the
    // ratios are blended. The band is centred on the old 14 threshold and sized to cover the
    // range ordinary UI text actually lands in.
    private const double SmallSourceHeight = 12.0;
    private const double LargeSourceHeight = 16.0;

    private const double SmallMultiplier = 1.18;
    private const double LargeMultiplier = 1.06;
    private const double SmallMinFontSize = 14.5;
    private const double LargeMinFontSize = 13.0;

    // A Latin source rendered as CJK gets a little extra: the translated glyphs carry more strokes
    // in the same em, so they lose legibility first. Capped in absolute terms so it stays a nudge.
    private const double SmallLatinToCjkBoost = 1.08;
    private const double LatinToCjkBoostCap = 1.25;

    /// <summary>
    /// How small the font for a source block of this height may be shrunk to fit the space
    /// available. Small source text has the higher floor: it is already near the readability limit,
    /// so it is the text that can least afford to give any size back.
    /// </summary>
    public static double MinFontSize(double sourceHeight) =>
        Blend(sourceHeight, SmallMinFontSize, LargeMinFontSize);

    /// <summary>
    /// Font size for a source block of <paramref name="sourceHeight"/>, before any fitting to the
    /// available bubble width.
    /// </summary>
    /// <param name="latinSourceToCjkTarget">
    /// True when a Latin source is being rendered in a CJK target script.
    /// </param>
    public static double Calculate(double sourceHeight, bool latinSourceToCjkTarget)
    {
        var multiplier = Blend(sourceHeight, SmallMultiplier, LargeMultiplier);
        var fontSize = sourceHeight * multiplier;

        if (latinSourceToCjkTarget)
        {
            // Fades out across the same band, so it cannot reintroduce a step of its own at the top.
            var boost = Blend(sourceHeight, SmallLatinToCjkBoost, 1.0);
            fontSize = Math.Min(fontSize * boost, fontSize + LatinToCjkBoostCap);
        }

        // The floor goes on last, so it stays a floor. Applying the fading boost on top of it
        // instead would make the font dip as the source grew, the boost shrinking faster than a
        // pinned-to-the-floor size can climb.
        //
        // SmallMinFontSize rather than MinFontSize(sourceHeight): that one falls with height, and
        // blending it in here would cause the same dip. Only the small end's floor ever binds on
        // this path anyway — height * LargeMultiplier clears LargeMinFontSize from 12.3px up.
        return Math.Max(SmallMinFontSize, fontSize);
    }

    private static double Blend(double sourceHeight, double atSmall, double atLarge)
    {
        var t = Math.Clamp(
            (sourceHeight - SmallSourceHeight) / (LargeSourceHeight - SmallSourceHeight), 0, 1);
        return atSmall + (atLarge - atSmall) * t;
    }
}
