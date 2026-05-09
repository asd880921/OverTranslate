using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OverTranslate.Services.Ocr;

public class WindowsOcrEngine : IOcrEngine
{
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "BG", "bg" }, { "CS", "cs" }, { "DA", "da" }, { "DE", "de" },
        { "EL", "el" }, { "EN", "en" }, { "ES", "es" }, { "ET", "et" },
        { "FI", "fi" }, { "FR", "fr" }, { "HU", "hu" }, { "ID", "id" },
        { "IT", "it" }, { "JA", "ja" }, { "KO", "ko" }, { "LT", "lt" },
        { "LV", "lv" }, { "NB", "nb" }, { "NL", "nl" }, { "PL", "pl" },
        { "PT", "pt" }, { "RO", "ro" }, { "RU", "ru" }, { "SK", "sk" },
        { "SL", "sl" }, { "SV", "sv" }, { "TR", "tr" }, { "UK", "uk" },
        { "ZH", "zh-Hans" }, { "ZH-HANT", "zh-Hant" }, { "ZH-HANS", "zh-Hans" },
    };

    // Windows OCR auto 候選語言，需系統已安裝語言包才會生效
    private static readonly string[] AutoLangTags = ["zh-Hans", "zh-Hant", "ja", "ko", "en"];

    private const int Padding = 20;

    public async Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        using var padded = AddWhitePadding(bitmap, Padding);
        var softwareBitmap = await ToSoftwareBitmapAsync(padded);

        if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return await RecognizeAutoAsync(softwareBitmap);

        OcrEngine? engine = null;
        if (LangMap.TryGetValue(sourceLanguage, out var tag))
            engine = OcrEngine.TryCreateFromLanguage(new Language(tag));

        engine ??= OcrEngine.TryCreateFromLanguage(new Language("en"));
        if (engine == null) return [];

        return await RunEngineAsync(engine, softwareBitmap, Padding);
    }

    // 對所有候選語言並行辨識，回傳辨識字元數最多的結果
    private static async Task<List<OcrTextBlock>> RecognizeAutoAsync(SoftwareBitmap softwareBitmap)
    {
        var engines = AutoLangTags
            .Select(t => OcrEngine.TryCreateFromLanguage(new Language(t)))
            .Where(e => e != null)
            .Cast<OcrEngine>()
            .ToList();

        if (engines.Count == 0) return [];

        var results = await Task.WhenAll(engines.Select(e => RunEngineAsync(e, softwareBitmap, Padding)));

        return results
            .OrderByDescending(r => r.Sum(b => b.Text.Length))
            .First();
    }

    private static async Task<List<OcrTextBlock>> RunEngineAsync(OcrEngine engine, SoftwareBitmap bitmap, int padding)
    {
        var result = await engine.RecognizeAsync(bitmap);
        var blocks = new List<OcrTextBlock>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var left   = line.Words.Min(w => w.BoundingRect.X) - padding;
            var top    = line.Words.Min(w => w.BoundingRect.Y) - padding;
            var right  = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width)  - padding;
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) - padding;
            blocks.Add(new OcrTextBlock(line.Text, new System.Windows.Rect(left, top, right - left, bottom - top)));
        }
        return blocks;
    }

    private static Bitmap AddWhitePadding(Bitmap source, int padding)
    {
        var result = new Bitmap(source.Width + padding * 2, source.Height + padding * 2);
        using var g = System.Drawing.Graphics.FromImage(result);
        g.Clear(System.Drawing.Color.White);
        g.DrawImage(source, padding, padding, source.Width, source.Height);
        return result;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
