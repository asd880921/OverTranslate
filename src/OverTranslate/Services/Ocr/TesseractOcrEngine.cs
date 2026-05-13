using System.Drawing;
using System.IO;
using NLog;
using Tesseract;

namespace OverTranslate.Services.Ocr;

public class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private TesseractEngine? _engine;
    private string? _currentLang;

    private static readonly string TessDataDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

    // Words below this Tesseract confidence (0-100) are discarded as noise / icon misdetections
    private const float MinWordConfidence = 60f;

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        var lang = MapLanguage(sourceLanguage);
        EnsureEngine(lang);

        return Task.Run(() =>
        {
            Log.Debug("Running Tesseract on {W}x{H} bitmap, lang={Lang}",
                bitmap.Width, bitmap.Height, lang);

            using var pix = BitmapToPix(bitmap);
            // SparseText: finds text scattered anywhere in the image without assuming document layout.
            // This is correct for UI screenshots where text elements are separated by large gaps.
            using var page = _engine!.Process(pix, PageSegMode.SparseText);

            var words = new List<OcrWord>();
            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                // Skip low-confidence detections — icons and non-text regions produce near-zero scores
                if (iter.GetConfidence(PageIteratorLevel.Word) < MinWordConfidence) continue;

                var text = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var b))
                    words.Add(new OcrWord(text.Trim(), new System.Windows.Rect(b.X1, b.Y1, b.Width, b.Height)));
            }
            while (iter.Next(PageIteratorLevel.Word));

            Log.Debug("Tesseract extracted {Count} words", words.Count);
            var blocks = TesseractBlockSegmenter.BuildBlocks(words, bitmap.Width, Log);
            Log.Debug("Tesseract returned {Count} blocks", blocks.Count);
            return blocks;
        });
    }

    private void EnsureEngine(string lang)
    {
        if (_engine != null && _currentLang == lang) return;

        Log.Info("Initialising TesseractEngine — tessdata dir: {Dir}, lang: {Lang}", TessDataDir, lang);
        Log.Debug("tessdata dir exists: {Exists}", Directory.Exists(TessDataDir));

        if (Directory.Exists(TessDataDir))
        {
            var files = Directory.GetFiles(TessDataDir, "*.traineddata");
            Log.Debug("traineddata files found ({Count}): {Files}",
                files.Length, string.Join(", ", files.Select(Path.GetFileName)));
        }
        else
        {
            Log.Warn("tessdata directory does not exist: {Dir}", TessDataDir);
        }

        _engine?.Dispose();
        try
        {
            _engine = new TesseractEngine(TessDataDir, lang, EngineMode.Default);
            _currentLang = lang;
            Log.Info("TesseractEngine initialised successfully for lang: {Lang}", lang);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TesseractEngine initialisation failed — dir={Dir}, lang={Lang}", TessDataDir, lang);
            throw;
        }
    }

    private static string MapLanguage(string code) => code.ToUpperInvariant() switch
    {
        "AUTO"                       => "chi_sim+chi_tra+jpn+kor+eng",
        "ZH" or "ZH-HANS"           => "chi_sim",
        "ZH-HANT"                    => "chi_tra",
        "JA"                         => "jpn",
        "KO"                         => "kor",
        "DE"                         => "deu",
        "FR"                         => "fra",
        "ES"                         => "spa",
        "IT"                         => "ita",
        "PT" or "PT-BR"              => "por",
        "RU"                         => "rus",
        "UK"                         => "ukr",
        "PL"                         => "pol",
        "NL"                         => "nld",
        "TR"                         => "tur",
        "AR"                         => "ara",
        _                            => "eng",
    };

    private static Pix BitmapToPix(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return Pix.LoadFromMemory(ms.ToArray());
    }

    public void Dispose() => _engine?.Dispose();
}
