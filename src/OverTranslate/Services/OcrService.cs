using System.Drawing;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(string Text, System.Windows.Rect Bounds);

public class OcrService : IDisposable
{
    private readonly TesseractOcrEngine _englishEngine = new();
    private readonly CjkOnnxOcrEngine _cjkEngine = new();

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        if (OcrLanguageRouter.UsesEnglishTesseract(sourceLanguage))
            return _englishEngine.RecognizeAsync(bitmap, "EN");

        if (OcrLanguageRouter.UsesCjkOnnx(sourceLanguage))
            return _cjkEngine.RecognizeAsync(bitmap, sourceLanguage);

        throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));
    }

    public void Dispose()
    {
        _englishEngine.Dispose();
        _cjkEngine.Dispose();
    }
}
