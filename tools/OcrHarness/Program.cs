using System.Drawing;
using GTranslate.Translators;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

// OCR + grouping (+ Microsoft translate) harness.
// Usage: OcrHarness <imagePath> [imagePath...]   (PNG/JPG screenshots)
// Prints the grouped blocks that would be sent to translation, then the EN->ZH-HANT result.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OcrHarness <image.png> [more.png ...]");
    return 1;
}

using var ocr = new OcrService();
var translator = new GTranslateProvider(new MicrosoftTranslator());

foreach (var path in args)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"IMAGE: {path}");
    if (!System.IO.File.Exists(path))
    {
        Console.WriteLine("  (file not found)");
        continue;
    }

    using var bitmap = new Bitmap(path);
    List<OcrTextBlock> blocks;
    try
    {
        blocks = await ocr.RecognizeAsync(bitmap, "EN");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  OCR FAILED: {ex.Message}");
        continue;
    }

    Console.WriteLine($"  grouped blocks: {blocks.Count}");
    for (var i = 0; i < blocks.Count; i++)
    {
        var b = blocks[i];
        Console.WriteLine(
            $"  [{i}] bounds=({b.Bounds.X:0},{b.Bounds.Y:0},{b.Bounds.Width:0},{b.Bounds.Height:0}) lines={b.Lines.Count}");
        Console.WriteLine($"       EN: {b.Text}");
    }

    try
    {
        var (translated, _) = await translator.TranslateAsync(blocks, "EN", "ZH-HANT", "");
        Console.WriteLine("  --- translations (Microsoft) ---");
        foreach (var t in translated)
            Console.WriteLine($"  EN: {t.OriginalText}\n  ZH: {t.TranslatedText}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  TRANSLATE FAILED: {ex.Message}");
    }
}

return 0;
