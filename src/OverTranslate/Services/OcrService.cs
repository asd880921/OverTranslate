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
    double? SourceGlyphHeight = null,
    // Mean per-character recognition confidence, 0–1. Reading the same unchanged text twice
    // gives two slightly different answers, and this is what says which to believe; null when
    // the engine reported no scores.
    double? Confidence = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];
}

public class OcrService : IDisposable
{
    private readonly OnnxOcrEngine _engine = new();

    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (OcrLanguageRouter.IsSupported(sourceLanguage))
            return RecognizeAndGroupAsync(
                _engine, bitmap, OcrLanguageRouter.Normalize(sourceLanguage), cancellationToken);

        throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));
    }

    /// <summary>
    /// Recognises only if the engine has a free slot right now, returning null instead of queueing.
    /// For callers watching a live screen, where a queued pass would be answering a frame that has
    /// already been replaced — see <see cref="IOcrEngine.TryRecognizeAsync"/>.
    /// </summary>
    public async Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        var blocks = await _engine.TryRecognizeAsync(
            bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken);

        return blocks is null ? null : OcrTextBlockGrouper.Group(blocks);
    }

    /// <summary>
    /// Keeps the loaded model in memory while a continuous caller is running — see
    /// <see cref="OnnxOcrEngine.SetKeepWarm"/>.
    /// </summary>
    public void SetKeepWarm(bool keepWarm) => _engine.SetKeepWarm(keepWarm);

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
}
