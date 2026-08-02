using System.Drawing;

namespace OverTranslate.Services.Ocr;

internal interface IOcrEngine : IDisposable
{
    Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default);
}
