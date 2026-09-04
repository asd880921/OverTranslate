using OverTranslate.Services;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Tests;

/// <summary>
/// Blocks as the engine hands them over, for tests that build them by hand instead of reading an
/// image.
/// </summary>
/// <remarks>
/// Nothing has run the normalisation that fills LayoutBounds / LayoutScript / LayoutGlyphHeight on
/// a hand-built block, and the grouper refuses one that arrives without them. Filling them here
/// with the production functions — rather than teaching the grouper to fall back to Bounds — keeps
/// the fixtures honest about what the grouper actually receives.
/// </remarks>
internal static class DetectedBlocks
{
    public static OcrTextBlock AsDetected(this OcrTextBlock block)
    {
        var script = LayoutScriptDetection.For(block.Text);
        return block with
        {
            LayoutScript = script,
            LayoutBounds = block.Bounds,
            LayoutGlyphHeight = OnnxOcrEngine.LayoutGlyphHeightFor(script, block.Bounds, block.Text),
        };
    }

    public static List<OcrTextBlock> AsDetected(this IEnumerable<OcrTextBlock> blocks) =>
        blocks.Select(block => block.AsDetected()).ToList();
}
