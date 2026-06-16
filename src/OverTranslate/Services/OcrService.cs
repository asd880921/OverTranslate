using System.Drawing;
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

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        if (OcrLanguageRouter.IsSupported(sourceLanguage))
            return RecognizeAndGroupAsync(_engine, bitmap, OcrLanguageRouter.Normalize(sourceLanguage));

        throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));
    }

    public void Dispose()
    {
        _engine.Dispose();
    }

    private static async Task<List<OcrTextBlock>> RecognizeAndGroupAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage)
    {
        var blocks = await engine.RecognizeAsync(bitmap, sourceLanguage);
        return OcrTextBlockGrouper.Group(blocks);
    }
}
