using System.Drawing;
using System.IO;
using GTranslate.Translators;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;

// OCR + grouping (+ Microsoft translate) harness.
// Usage: OcrHarness <imagePath> [imagePath...]   (PNG/JPG screenshots)
// Prints the grouped blocks that would be sent to translation, then the EN->ZH-HANT result.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OcrHarness <image.png> [more.png ...]");
    Console.Error.WriteLine("       OcrHarness --xlate-line <text>   (translate one line, all engines)");
    Console.Error.WriteLine("       OcrHarness --compare-models <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame and size, cjk vs korean recognition model)");
    Console.Error.WriteLine("       OcrHarness --scale-sweep [--det <det.onnx>[:imagenet|:half]] <image.png> [...]");
    Console.Error.WriteLine("                  (reads each frame at every detector size, no translation)");
    Console.Error.WriteLine("       OcrHarness --xlate-test   (network translation/resilience check, no OCR)");
    return 1;
}

// Forced-fallback check: the primary engine gets a 1ms-timeout HttpClient so it always fails,
// proving the hedge falls back to a backup engine and that the badge data (FallbackUsed/Dominant)
// is computed correctly. No OCR / screenshot needed.
if (args[0] == "--fallback-test")
{
    var samples = new List<OcrTextBlock>
    {
        new("Settings have been saved successfully.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Please restart the application.",        new System.Windows.Rect(0, 0, 100, 20)),
    };

    var brokenHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(1) };
    var resilient = new ResilientProvider(
        [
            new GTranslateProvider(new MicrosoftTranslator(brokenHttp)), // primary: always times out
            new GTranslateProvider(new GoogleTranslator2()),             // backup
            new GTranslateProvider(new BingTranslator()),                // backup
        ],
        hedgeDelay: TimeSpan.FromMilliseconds(200));

    var (translated, _) = await resilient.TranslateAsync(samples, "EN", "ZH-HANT", "");
    var u = resilient.LastUsage!;
    Console.WriteLine($"FallbackUsed = {u.FallbackUsed}  (expected True)");
    Console.WriteLine($"Primary      = {u.Primary}       (expected: Microsoft)");
    Console.WriteLine($"BackupEngine = {u.BackupEngine}  (expected: a backup, not Microsoft)");
    Console.WriteLine($"Summary      = {u.Summary}");
    Console.WriteLine($"Badge would show: ⚡備援 {u.BackupEngine}");
    foreach (var t in translated)
        Console.WriteLine($"  ZH: {t.TranslatedText}");
    return 0;
}

// Translate one line given on the command line. For telling an OCR problem from a translation
// one: when a word goes missing on screen, this says whether the recogniser dropped it or the
// translator did.
if (args[0] == "--xlate-line")
{
    var line = string.Join(' ', args.Skip(1));
    if (line.Length == 0)
    {
        Console.Error.WriteLine("usage: OcrHarness --xlate-line <text to translate>");
        return 1;
    }

    var block = new List<OcrTextBlock> { new(line, new System.Windows.Rect(0, 0, 100, 20)) };
    foreach (var (name, provider) in new (string, GTranslateProvider)[]
             {
                 ("Microsoft", new GTranslateProvider(new MicrosoftTranslator())),
                 ("Google   ", new GTranslateProvider(new GoogleTranslator2())),
                 ("Bing     ", new GTranslateProvider(new BingTranslator())),
             })
    {
        try
        {
            var (result, _) = await provider.TranslateAsync(block, "EN", "ZH-HANT", "");
            Console.WriteLine($"  [{name}] {result[0].TranslatedText}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{name}] FAILED: {ex.Message}");
        }
    }

    return 0;
}

// Translation-only resilience check: exercises ResilientProvider over the live free endpoints
// and reports per-run latency. No OCR / screenshot needed.
if (args[0] == "--xlate-test")
{
    var samples = new List<OcrTextBlock>
    {
        new("The quick brown fox jumps over the lazy dog.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Settings have been saved successfully.",        new System.Windows.Rect(0, 0, 100, 20)),
        new("Please restart the application to apply changes.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Translation speed should now be more consistent.", new System.Windows.Rect(0, 0, 100, 20)),
    };

    var resilient = new ResilientProvider([
        new GTranslateProvider(new GoogleTranslator2()),
        new GTranslateProvider(new BingTranslator()),
        new GTranslateProvider(new MicrosoftTranslator()),
    ]);

    for (var run = 1; run <= 3; run++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (translated, detected) = await resilient.TranslateAsync(samples, "EN", "ZH-HANT", "");
        sw.Stop();
        Console.WriteLine($"--- run {run}: {sw.ElapsedMilliseconds} ms (detected={detected}) ---");
        Console.WriteLine($"  實際使用引擎: {resilient.LastBatchSummary}");
        foreach (var t in translated)
            Console.WriteLine($"  EN: {t.OriginalText}\n  ZH: {t.TranslatedText}");
        Console.WriteLine();
    }
    return 0;
}

// Model comparison: the same frame, the same detector size, only the recognition model changed.
// "EN" routes to the general cjk model and "KO" to the korean one (see GetModelKeyForLanguage),
// which makes korean a natural control: both are large-dictionary models, and the only structural
// difference is that korean holds no Han characters at all. If Latin reads better under it, the
// 15,702 Han characters competing for every glyph are the problem, and a Latin-only dictionary
// would help more still.
if (args[0] == "--compare-models")
{
    using var cmpOcr = new OcrService();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var native = Math.Max(image.Width, image.Height);
        var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Path.GetFileName(path)}  {image.Width}x{image.Height}");

        // Both sizes, because the model is not the only thing that decides whether a line is read:
        // comparing at one size risks reporting a detector result as a recognition difference.
        foreach (var size in new[] { primary, native }.Distinct())
        {
            Console.WriteLine($"  --- detect={size}{(size == primary ? " (realtime primary)" : " (native)")} ---");

            foreach (var (label, lang) in new[] { ("cjk   ", "EN"), ("korean", "KO") })
            {
                List<OcrTextBlock>? blocks = null;
                for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
                    blocks = await cmpOcr.TryRecognizeAsync(image, lang, size);

                if (blocks is null || blocks.Count == 0)
                {
                    Console.WriteLine($"    [{label}] (nothing read)");
                    continue;
                }

                foreach (var b in blocks)
                {
                    var confidence = b.Confidence is { } c ? $"{c:0.00}" : "  - ";
                    Console.WriteLine($"    [{label}] score={confidence}  \"{b.Text.Replace("\n", " ")}\"");
                }
            }
        }
    }

    return 0;
}

// Scale sweep: the same frame read at every detector size, to see which sizes find the text and
// how wide the working band is. This is the measurement #22 step 0 asks for, and it only means
// anything on frames whose contents are known — the dumped "rescued" frames, where the primary
// size read nothing and a fallback read the subtitle fine.
if (args[0] == "--scale-sweep")
{
    var sweepArgs = args.Skip(1).ToList();

    // Which detector to sweep with. Without it the sweep uses the shipped one, which is what every
    // measurement in #22 up to now was made with — so the flag's absence reproduces those numbers
    // and its presence is the only thing that differs.
    if (sweepArgs.FirstOrDefault() == "--det")
    {
        if (sweepArgs.Count < 2)
        {
            Console.Error.WriteLine("usage: --scale-sweep --det <det.onnx>[:imagenet|:half] <image.png> ...");
            return 1;
        }

        var spec = sweepArgs[1];
        sweepArgs.RemoveRange(0, 2);

        // The normalisation travels with the model, so it is named next to it rather than left to
        // a default: a v6 detector read with v5's statistics is a different, worse detector, and
        // the mistake is invisible in the output — it just reads less.
        var separator = spec.LastIndexOf(':');
        var normalization = separator > 1 ? spec[(separator + 1)..] : "imagenet";
        var detPath = separator > 1 ? spec[..separator] : spec;

        OnnxOcrEngine.DetectorOverride = normalization switch
        {
            "imagenet" => new OnnxOcrEngine.DetectorModel(
                detPath, OnnxOcrEngine.ImageNetNormalization, OnnxOcrEngine.ImageNetNormalizationStd),
            "half" => new OnnxOcrEngine.DetectorModel(
                detPath, OnnxOcrEngine.HalfNormalization, OnnxOcrEngine.HalfNormalization),
            _ => null,
        };

        if (OnnxOcrEngine.DetectorOverride is null)
        {
            Console.Error.WriteLine($"unknown normalization \"{normalization}\" (expected imagenet or half)");
            return 1;
        }

        Console.WriteLine($"detector: {detPath}  normalization={normalization}");
    }
    else
    {
        Console.WriteLine("detector: shipped (ocrmodels/onnx/shared/det.onnx)  normalization=imagenet");
    }

    using var sweepOcr = new OcrService();

    foreach (var path in sweepArgs)
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var native = Math.Max(image.Width, image.Height);
        var aspect = image.Height > 0 ? (double)image.Width / image.Height : 0;

        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  ratio={aspect:0.00}");

        // What the app itself would pick for this block, so the sweep can be read against it.
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height);
        Console.WriteLine($"  app picks: primary={primary} fallbacks=[{string.Join(",", fallbacks)}]");

        // Stepped in whole percent, not by adding 0.05 to a double: the accumulated error moved
        // 0.50 to 0.4999, which rounds to a different 32-pixel stride and quietly sweeps a size the
        // app would never ask for — right where this measurement is most sensitive.
        for (var percent = 30; percent <= 100; percent += 5)
        {
            var fraction = percent / 100.0;
            // Same rounding the app uses, so the numbers here are the ones it would really ask for.
            var size = fraction >= 1.0 ? native : Math.Max(320, ((int)(native * fraction) + 31) / 32 * 32);

            List<OcrTextBlock>? blocks = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
            {
                elapsed.Restart();
                blocks = await sweepOcr.TryRecognizeAsync(image, "EN", size);
            }

            elapsed.Stop();

            var mark = size == primary ? " <- primary" : fallbacks.Contains(size) ? " <- fallback" : "";
            var text = blocks is null || blocks.Count == 0
                ? ""
                : "  " + string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));

            // chars and ms, because neither question this sweep exists for can be answered without
            // both: a size is only counted as reading the subtitle past a character floor (icons and
            // scenery misreads come back short), and the size that reads most is not the size to pick
            // if it costs several times as much per pass.
            var chars = blocks?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;

            Console.WriteLine(
                $"  {fraction:0.00} -> {size,5} : {blocks?.Count ?? -1} box chars={chars,3} " +
                $"{elapsed.ElapsedMilliseconds,5}ms{mark}{text}");
        }
    }

    return 0;
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
