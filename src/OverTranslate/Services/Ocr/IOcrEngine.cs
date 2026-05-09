using System.Drawing;

namespace OverTranslate.Services.Ocr;

public interface IOcrEngine
{
    Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage);
}
