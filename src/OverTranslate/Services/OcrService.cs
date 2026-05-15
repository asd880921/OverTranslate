using System.Drawing;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(
    string Text,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];
}

public class OcrService : IDisposable
{
    private readonly TesseractOcrEngine _englishEngine = new();
    private readonly CjkOnnxOcrEngine _cjkEngine = new();

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        if (OcrLanguageRouter.UsesEnglishTesseract(sourceLanguage))
            return RecognizeAndGroupAsync(_englishEngine, bitmap, "EN");

        if (OcrLanguageRouter.UsesCjkOnnx(sourceLanguage))
            return RecognizeAndGroupAsync(_cjkEngine, bitmap, sourceLanguage);

        throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));
    }

    public void Dispose()
    {
        _englishEngine.Dispose();
        _cjkEngine.Dispose();
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
