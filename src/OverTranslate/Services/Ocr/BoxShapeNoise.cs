namespace OverTranslate.Services.Ocr;

/// <summary>
/// Rejects a box far too wide for the characters read out of it, which is a misdetection rather
/// than text.
/// </summary>
/// <remarks>
/// Characters are about as wide as they are tall, or narrower. So the width a box spends per
/// character, measured against its own height, says whether what came back can be what is in it: a
/// line of any length lands near or under 1, and a box six times wider than tall holding one
/// character is not holding one character.
///
/// Measured over 2533 recognised blocks from live sessions and screenshots. At a threshold of 2 the
/// rule takes 160 of them, and every single one is rubbish — 69 lone □, 57 □2, and singles like
/// [, ], A, S, K, W, [0]2| — while real short words sit far below it:
///
/// <code>
///   width per character / box height     blocks   examples
///   0.0 - 0.8                              1416   "It's so relaxing.", "Yay!", "What?!"
///   0.8 - 1.4                               942   お知らせ, 知らせ, 仲町あられ, 26.07.30
///   1.4 - 2.0                                15   K, 🌟, A, <, -, mm
///   2.0 and over                            160   □, □2, 🎶□2, [, [0]2|
/// </code>
///
/// The gap between the real words at 1.4 and the rubbish at 2.0 is what the threshold sits in. It
/// could be pulled down to 1.5 and take the 15 blocks between them, which are also rubbish, but
/// that is a twentieth of the benefit for most of the margin.
///
/// This matters more than the count suggests. Such a block is short, so it looks nothing like the
/// subtitle already on screen, so it counts as a new sentence — and a region whose only reading is
/// a lone □ replaces a correct translation with one.
///
/// The measurement is on <c>LayoutBounds</c>, the box the detector drew, because this runs after
/// normalisation and <c>Bounds</c> by then means different things on different source languages: a
/// CJK-normalised box is 0.82 as tall, so the same detection reads 1.22x higher and can cross the
/// threshold on one language and not another. Measured over 2710 blocks of the image corpus, the
/// rule took 17 of them under 英文, 32 under 日文 and 18 under 自動 while reading Bounds — and 14
/// under all three while reading LayoutBounds. The eighteen 日文 lost that way were labels like
/// 闇, 三, 決定 and 결정, which is the whole of what PR #161 was about.
/// </remarks>
internal static class BoxShapeNoise
{
    /// <summary>
    /// Width per character, as a multiple of the box's height, past which the box cannot be holding
    /// the text that was read from it.
    /// </summary>
    public const double MaxWidthPerGlyph = 2.0;

    public static bool IsTooWideForItsText(OcrTextBlock block)
    {
        var glyphs = ShortTextGlyphHeight.GlyphsIn(block.Text);
        if (glyphs == 0 || block.LayoutBounds.Height <= 0) return false;

        return block.LayoutBounds.Width / glyphs / block.LayoutBounds.Height >= MaxWidthPerGlyph;
    }
}
