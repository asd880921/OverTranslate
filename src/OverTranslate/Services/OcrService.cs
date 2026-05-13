using System.Drawing;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(string Text, System.Windows.Rect Bounds);

public class OcrService : IDisposable
{
    private readonly TesseractOcrEngine _engine = new();

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage) =>
        _engine.RecognizeAsync(bitmap, sourceLanguage);

    public void Dispose() => _engine.Dispose();
}
