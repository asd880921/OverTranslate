namespace OverTranslate.Services.Ocr;

/// <summary>
/// What the user says this capture holds, chosen on the toolbar before translating.
/// </summary>
/// <remarks>
/// <para>Not the same thing as <see cref="OcrLayoutScript"/>, and the two must not be derived from
/// one another. The script is a property of the text the detector read; this is a property of the
/// capture the user framed, and it is the user who knows whether the box is a comic panel or a
/// game menu. It decides two things and nothing else: how far the grouping thresholds are relaxed,
/// and whether the translation is laid back onto each source line or set as one block.</para>
///
/// <para>Deliberately not carried on <see cref="OcrTextBlock"/>. A block is not standard or
/// comic — the capture is. Putting it there is how one field ends up meaning four things, which is
/// what the layout-metric split was spent undoing.</para>
/// </remarks>
public enum CaptureLayoutMode
{
    /// <summary>Interface, game UI, multi-column content: leave the arrangement as it is.</summary>
    Standard,

    /// <summary>Comics and prose: read in order, and relax the merge tests a little.</summary>
    ComicArticle,
}
