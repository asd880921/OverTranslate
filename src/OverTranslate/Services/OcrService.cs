using System.Drawing;
using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(
    string Text,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    // Visual glyph height (physical px) used to size the overlay font, kept separate from
    // Bounds. For Latin source the detection box is much taller than the rendered CJK font,
    // so Bounds stays full (for background coverage) while this drives only the font size.
    // Null for CJK, where Bounds already matches the glyph height.
    double? SourceGlyphHeight = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];
}

public class OcrService : IDisposable
{
    private readonly OnnxOcrEngine _engine = new();

    /// <param name="verticalText">
    /// Set for pages written top-to-bottom, as Japanese comics are. The detector only finds
    /// horizontal runs, so on a vertical page it returns almost nothing and the few boxes it does
    /// return come back as gibberish. Turning the page a quarter turn first fixes both — measured
    /// on a sample page, 2 unusable blocks became 21 correct ones — with no change to any model or
    /// threshold, so the default path behaves exactly as it always has.
    /// </param>
    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        bool verticalText = false)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        var language = OcrLanguageRouter.Normalize(sourceLanguage);

        return verticalText
            ? RecognizeVerticalAsync(_engine, bitmap, language, cancellationToken)
            : RecognizeAndGroupAsync(_engine, bitmap, language, cancellationToken);
    }

    public void Dispose()
    {
        _engine.Dispose();
    }

    private static async Task<List<OcrTextBlock>> RecognizeAndGroupAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var blocks = await engine.RecognizeAsync(bitmap, sourceLanguage, cancellationToken);
        return OcrTextBlockGrouper.Group(blocks);
    }

    /// <summary>
    /// Reads a vertical page by turning it a quarter turn anticlockwise, running the ordinary
    /// pipeline, then mapping the results back. Anticlockwise specifically: clockwise finds
    /// nothing, and this direction also yields the columns in reading order, because the rightmost
    /// column — where a reader starts — ends up at the top.
    /// </summary>
    private static async Task<List<OcrTextBlock>> RecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        // Grouping runs while the page is still turned, so adjacent columns merge under the same
        // left-to-right rules that already work for horizontal text.
        using var rotated = new Bitmap(bitmap);
        rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);

        var blocks = await RecognizeAndGroupAsync(engine, rotated, sourceLanguage, cancellationToken);

        return blocks.Select(block => block with
        {
            Bounds = MapBack(block.Bounds, bitmap.Width),
            SourceLineBounds = block.SourceLineBounds?.Select(line => MapBack(line, bitmap.Width)).ToList(),
            // Turned back, a column is tall and narrow, so its width — not its height — is the size
            // the glyphs were actually drawn at.
            SourceGlyphHeight = block.Bounds.Height,
        }).ToList();
    }

    /// <summary>
    /// Undoes the quarter turn for one rectangle: a point at (x, y) on the turned page came from
    /// (width - y, x) on the original, so the rectangle's sides swap.
    /// </summary>
    private static Rect MapBack(Rect rotated, int originalWidth) => new(
        originalWidth - (rotated.Y + rotated.Height),
        rotated.X,
        rotated.Height,
        rotated.Width);
}
