using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(string Text, System.Windows.Rect Bounds);

public class OcrService : IDisposable
{
    private IOcrEngine? _cachedEngine;
    private OcrEngineType _cachedType;

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        var engineType = SettingsService.Instance.Current.OcrEngine;
        return GetOrCreateEngine(engineType).RecognizeAsync(bitmap, sourceLanguage);
    }

    private IOcrEngine GetOrCreateEngine(OcrEngineType type)
    {
        if (_cachedEngine != null && _cachedType == type)
            return _cachedEngine;

        (_cachedEngine as IDisposable)?.Dispose();
        _cachedEngine = type == OcrEngineType.Tesseract
            ? (IOcrEngine)new TesseractOcrEngine()
            : new WindowsOcrEngine();
        _cachedType = type;
        return _cachedEngine;
    }

    public void Dispose() => (_cachedEngine as IDisposable)?.Dispose();
}
