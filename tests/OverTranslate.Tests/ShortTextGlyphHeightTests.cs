using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Both overlays size their font from the glyph height this corrects, so a mistake here is text
/// drawn at the wrong size over the user's screen — in the screenshot flow as much as the live one.
/// </summary>
public class ShortTextGlyphHeightTests
{
    [Fact]
    public void ATwoLetterLineIsSizedFromItsBoxRatherThanTheOverblownEstimate()
    {
        // The measured case: "YA" drawn at a 64px em came back in a 95px box, reported as 78
        // against a true glyph height of about 46.
        Assert.Equal(47.5, ShortTextGlyphHeight.For(estimated: 78, boxHeight: 95, glyphCount: 2));
    }

    [Fact]
    public void ALongLineIsLeftAloneBecauseThePitchClampAlreadyCorrectedIt()
    {
        // "Hello there friend" at the same em: pitch had already brought 70.5 down to 45.5, which
        // is right to within a pixel, and halving its box would take it to 43 for no reason.
        Assert.Equal(45.5, ShortTextGlyphHeight.For(estimated: 45.5, boxHeight: 86, glyphCount: 16));
    }

    [Fact]
    public void TheCorrectionOnlyEverMakesTextSmaller()
    {
        // A tight box on short text needs no correction, and this must never invent height.
        Assert.Equal(20, ShortTextGlyphHeight.For(estimated: 20, boxHeight: 60, glyphCount: 2));
    }

    [Fact]
    public void AMissingBoxLeavesTheEstimateUntouched()
    {
        Assert.Equal(30, ShortTextGlyphHeight.For(estimated: 30, boxHeight: 0, glyphCount: 1));
    }

    [Fact]
    public void TheBoundaryMatchesTheClampItStandsInFor()
    {
        // Three glyphs is the last length the pitch clamp does not cover, so it is the last one
        // corrected here. Off by one in either direction and a length is either corrected twice or
        // not at all.
        Assert.Equal(30, ShortTextGlyphHeight.For(estimated: 60, boxHeight: 60, glyphCount: 3));
        Assert.Equal(60, ShortTextGlyphHeight.For(estimated: 60, boxHeight: 60, glyphCount: 4));
    }
}
