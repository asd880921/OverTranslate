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

            var words = new List<(string Text, System.Windows.Rect Bounds)>();
            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                // Skip low-confidence detections — icons and non-text regions produce near-zero scores
                if (iter.GetConfidence(PageIteratorLevel.Word) < MinWordConfidence) continue;

                var text = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var b))
                    words.Add((text.Trim(), new System.Windows.Rect(b.X1, b.Y1, b.Width, b.Height)));
            }
            while (iter.Next(PageIteratorLevel.Word));

            var blocks = ClusterWordsIntoBlocks(words);
            Log.Debug("Tesseract returned {Count} blocks", blocks.Count);
            return blocks;
        });
    }

    // Groups words into blocks by proximity: words on the same row and within a gap threshold
    // are merged into one block. Words separated by a gap larger than 1.5× average word height
    // are treated as distinct UI elements (e.g. title-bar sections, left/right UI columns).
    private static List<OcrTextBlock> ClusterWordsIntoBlocks(
        List<(string Text, System.Windows.Rect Bounds)> words)
    {
        if (words.Count == 0) return [];

        var sorted = words.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();

        // Group into rows: two words share a row when their vertical ranges overlap
        var rows = new List<List<(string Text, System.Windows.Rect Bounds)>>();
        foreach (var word in sorted)
        {
            bool added = false;
            foreach (var row in rows)
            {
                double rowTop    = row.Min(w => w.Bounds.Y);
                double rowBottom = row.Max(w => w.Bounds.Y + w.Bounds.Height);
                double wordBottom = word.Bounds.Y + word.Bounds.Height;
                if (word.Bounds.Y < rowBottom && wordBottom > rowTop)
                {
                    row.Add(word);
                    added = true;
                    break;
                }
            }
            if (!added)
                rows.Add([(word.Text, word.Bounds)]);
        }

        var blocks = new List<OcrTextBlock>();
        foreach (var row in rows)
        {
            var rowSorted = row.OrderBy(w => w.Bounds.X).ToList();
            double avgH = rowSorted.Average(w => w.Bounds.Height);
            double gapThreshold = Math.Max(avgH * 1.5, 20);

            var cluster = new List<(string Text, System.Windows.Rect Bounds)> { rowSorted[0] };
            for (int i = 1; i < rowSorted.Count; i++)
            {
                var prev = rowSorted[i - 1];
                var curr = rowSorted[i];
                double gap = curr.Bounds.X - (prev.Bounds.X + prev.Bounds.Width);
                if (gap > gapThreshold)
                {
                    blocks.Add(BuildBlock(cluster));
                    cluster = [];
                }
                cluster.Add(curr);
            }
            if (cluster.Count > 0)
                blocks.Add(BuildBlock(cluster));
        }
        return blocks;
    }

    private static OcrTextBlock BuildBlock(List<(string Text, System.Windows.Rect Bounds)> words)
    {
        var text  = string.Join(" ", words.Select(w => w.Text));
        double x  = words.Min(w => w.Bounds.X);
        double y  = words.Min(w => w.Bounds.Y);
        double x2 = words.Max(w => w.Bounds.X + w.Bounds.Width);
        double y2 = words.Max(w => w.Bounds.Y + w.Bounds.Height);
        return new OcrTextBlock(text, new System.Windows.Rect(x, y, x2 - x, y2 - y));
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
