namespace OverTranslate.Services.Ocr;

/// <summary>
/// Which quantity the size test compared two lines on, and — where it was not the estimated glyph
/// height — which of the two reasons put it on the detection boxes instead.
/// </summary>
/// <remarks>
/// The two fallbacks look identical in the output and need opposite fixes. A pair that fell back
/// because one side carries no estimate is fixed by estimating that side; a pair whose scripts
/// differ falls back with both estimates in hand, because matching scripts is the first thing the
/// test asks about — so supplying the missing estimate would change nothing at all. One round of
/// this branch was spent proposing the first fix for a pair that was the second.
/// </remarks>
internal enum TextSizeBasis
{
    /// <summary>Not asked: a same-line verdict has no size test.</summary>
    None,

    /// <summary>The estimated glyph heights, which is the comparison the test is written for.</summary>
    Glyph,

    /// <summary>The detection boxes, because the two lines are not of the same script.</summary>
    BoxDifferentScript,

    /// <summary>The detection boxes, because one side of a same-script pair carries no estimate.</summary>
    BoxNoGlyphHeight,
}
