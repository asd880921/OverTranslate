using System.Drawing;
using System.IO;
using GTranslate.Translators;
using OverTranslate.Layout;
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
    Console.Error.WriteLine("       OcrHarness --scale-sweep <image.png> [...]");
    Console.Error.WriteLine("                  (reads each frame at every detector size, no translation)");
    Console.Error.WriteLine("       OcrHarness --margin-series <wholescreen.png> [more.png ...]");
    Console.Error.WriteLine("                  (whole screens: the same subtitle framed at a range of margins)");
    Console.Error.WriteLine("       OcrHarness --thresh-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame, detector box thresholds moved one axis at a time)");
    Console.Error.WriteLine("       OcrHarness --pad-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame and size, a range of borders around it)");
    Console.Error.WriteLine("       OcrHarness --margin-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same text, blocks cropped tight around it vs left loose)");
    Console.Error.WriteLine("       OcrHarness --margin-scale-grid <wholescreen.png> [more.png ...]");
    Console.Error.WriteLine("                  (CSV: the same subtitle at several margins, each read at every scale)");
    Console.Error.WriteLine("       OcrHarness --group-explain <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (every next-line verdict with the geometry it judged on)");
    Console.Error.WriteLine("       OcrHarness --reject-audit <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (what the confidence filter drops, and what a line would have reclaimed)");
    Console.Error.WriteLine("       OcrHarness --xlate-test   (network translation/resilience check, no OCR)");
    Console.Error.WriteLine("       (add --panel / --lang KO / --size N / --det model.onnx:half to any sweep)");
    return 1;
}

// Which mode to read as. The application asks the user this (see RealtimeBlockMode) because the
// answer is about what is on screen and not about the picture's shape; offline there is nobody to
// ask, so it is a flag. Subtitle by default: every dump kept so far is one, and reading a subtitle
// dump as a panel would report the wrong half of RealtimeDetectorSize.
var harnessMode = args.Contains("--panel") ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
args = [.. args.Where(argument => argument != "--panel")];

// Which language to read as. It picks the recognition model, and that is not a detail on a Korean
// dump: the general model carries no Hangul at all, so a Korean frame read as EN comes back as
// whatever Latin and Han the recogniser can force onto the shapes. The detector is shared, so this
// changes nothing about detection — which is the point when the thing under test is a detector.
var harnessLanguage = "EN";
var languageFlag = Array.IndexOf(args, "--lang");
if (languageFlag >= 0)
{
    if (languageFlag + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --lang <EN|JA|KO|AUTO|...>");
        return 1;
    }

    harnessLanguage = args[languageFlag + 1].ToUpperInvariant();
    args = [.. args.Take(languageFlag), .. args.Skip(languageFlag + 2)];
}

// The detector size to read at, or null to let the mode decide. #71 needed this: the fault it was
// chasing only appears at the large sizes the realtime fallbacks use, and every sweep here either
// walked all the sizes or pinned one of its own, so there was no way to hold the size still and
// vary something else.
int? harnessSize = null;
var sizeFlag = Array.IndexOf(args, "--size");
if (sizeFlag >= 0)
{
    if (sizeFlag + 1 >= args.Length || !int.TryParse(args[sizeFlag + 1], out var parsedSize))
    {
        Console.Error.WriteLine("usage: --size <detector input long side in px>");
        return 1;
    }

    harnessSize = parsedSize;
    args = [.. args.Take(sizeFlag), .. args.Skip(sizeFlag + 2)];
}

// Which detector to read with. Without it every mode uses the shipped one, so the flag's absence
// reproduces the shipped numbers and its presence is the only thing that differs.
var detectorFlag = Array.IndexOf(args, "--det");
if (detectorFlag >= 0)
{
    if (detectorFlag + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --det <det.onnx>[:imagenet|:half]");
        return 1;
    }

    var spec = args[detectorFlag + 1];
    args = [.. args.Take(detectorFlag), .. args.Skip(detectorFlag + 2)];

    // The normalisation travels with the model, so it is named next to it rather than left to a
    // default: a v6 detector read with v5's statistics is a different, worse detector, and the
    // mistake is invisible in the output — it just reads less.
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

    // All four, named apart. "Google" is two different endpoints and they need not behave alike —
    // the resilient chains use GoogleTranslator2 as a backup and GoogleTranslator as a primary, so
    // a limit measured on one says nothing about the other.
    //
    // --raw hands each engine the whole text, bypassing TranslationRequestChunks. That is what
    // reproduces the fault the chunking exists to prevent, so it stays available: without it the
    // only way to see an endpoint's real behaviour past its limit is to delete the fix.
    var raw = args.Contains("--raw");
    var line2 = raw ? string.Join(' ', args.Skip(1).Where(a => a != "--raw")) : line;
    var limit = raw ? int.MaxValue : (int?)null;

    var block = new List<OcrTextBlock> { new(line2, new System.Windows.Rect(0, 0, 100, 20)) };
    Console.WriteLine($"  input: {line2.Length} chars, chunking {(raw ? "OFF" : "ON")}");

    foreach (var (name, provider) in new (string, GTranslateProvider)[]
             {
                 ("Microsoft", new GTranslateProvider(new MicrosoftTranslator(), null, limit)),
                 ("Google Web", new GTranslateProvider(new GoogleTranslator(), null, limit)),
                 ("Google RPC", new GTranslateProvider(new GoogleTranslator2(), null, limit)),
                 ("Bing      ", new GTranslateProvider(new BingTranslator(), null, limit)),
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
        var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

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

// Export: whole screens in, one crop per subtitle out, for use as a fixture set.
//
// Comparing two detectors needs frames neither of them chose. A region dump is not that — it was
// kept because the shipped detector did or did not read it — and a whole screen is not either, since
// most of it is not the text under test. Locating the subtitle once, with the shipped detector, and
// writing that crop out gives every later run the same input: the location is decided by one model
// but the crop is then fixed, so no candidate is being scored on a frame chosen to suit it.
//
// The margin is the one --margin-series measured as best over 27 frames, not a round number.
if (args[0] == "--export-subtitle")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: --export-subtitle <outputDir> <wholescreen.png> [more.png ...]");
        return 1;
    }

    const double SubtitleBandTop = 0.62;
    const int ExportMargin = 60;

    var outputDir = args[1];
    System.IO.Directory.CreateDirectory(outputDir);
    using var exportOcr = new OcrService();
    var written = 0;

    foreach (var path in args.Skip(2))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileNameWithoutExtension(path);

        var whole = await exportOcr.TryRecognizeAsync(image, "AUTO") ?? [];
        var band = whole
            .Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop)
            .ToList();
        if (band.Count == 0) { Console.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        var union = System.Windows.Rect.Empty;
        foreach (var block in band)
        {
            var bounds = block.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            if (Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5 &&
                overlap > Math.Min(bounds.Width, anchor.Width) * 0.5)
                union.Union(bounds);
        }

        var x = (int)Math.Max(0, union.X - ExportMargin);
        var y = (int)Math.Max(0, union.Y - ExportMargin);
        var w = (int)Math.Min(image.Width - x, union.Width + ExportMargin * 2);
        var h = (int)Math.Min(image.Height - y, union.Height + ExportMargin * 2);
        if (w <= 1 || h <= 1) { Console.WriteLine($"{name}: degenerate crop, skipped"); continue; }

        using var crop = image.Clone(new Rectangle(x, y, w, h), image.PixelFormat);
        var target = Path.Combine(outputDir, $"{name}-sub.png");
        crop.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        written++;
        Console.WriteLine($"{name}: {w}x{h} -> {Path.GetFileName(target)}");
    }

    Console.WriteLine($"{written} crops written to {outputDir}");
    return 0;
}

// Margin series: one whole screen, cropped around its subtitle at a range of margins, read at each.
//
// The question is what the user's own framing costs. Every other sweep here starts from a region
// somebody already drew and can only change what is done to it; this one starts from the picture the
// region was drawn on, so the framing itself is the variable. It needs whole screens rather than
// region dumps for that reason — a dump has already had its margin chosen.
//
// The subtitle is located rather than given: reading a full screen finds it along with whatever else
// is on the picture, and the boxes in the bottom third of a video frame are the subtitle. The union
// of those is then re-read once with room around it, because the first pass is subject to the very
// effect being measured and its box may already be short at one end.
if (args[0] == "--margin-series")
{
    using var marginOcr = new OcrService();

    // Where a subtitle lives, as a fraction of the frame's height. Deliberately generous: a two-line
    // subtitle on a 16:9 frame starts around 0.82, and a frame with the line higher up is better
    // skipped by the character floor below than caught by a boundary drawn tightly here.
    const double SubtitleBandTop = 0.62;

    // Frames whose best reading is shorter than this are not carrying a subtitle to measure, and a
    // series over one of them reports noise moving around rather than framing.
    const int MinChars = 8;

    int[] margins = [0, 10, 20, 30, 40, 60, 80, 120];
    var totals = new int[margins.Length];
    var perfect = new int[margins.Length];
    var measured = 0;

    async Task<List<OcrTextBlock>> ReadAsync(Bitmap source, System.Windows.Rect area)
    {
        var x = (int)Math.Max(0, area.X);
        var y = (int)Math.Max(0, area.Y);
        var w = (int)Math.Min(source.Width - x, area.Width);
        var h = (int)Math.Min(source.Height - y, area.Height);
        if (w <= 1 || h <= 1) return [];

        using var crop = source.Clone(new Rectangle(x, y, w, h), source.PixelFormat);
        return await marginOcr.TryRecognizeAsync(crop, "AUTO") ?? [];
    }

    static System.Windows.Rect Union(IEnumerable<OcrTextBlock> blocks, double offsetX, double offsetY)
    {
        var union = System.Windows.Rect.Empty;
        foreach (var block in blocks)
        {
            var bounds = block.Bounds;
            bounds.Offset(offsetX, offsetY);
            union.Union(bounds);
        }

        return union;
    }

    static System.Windows.Rect Grow(System.Windows.Rect area, int margin) => new(
        area.X - margin, area.Y - margin, area.Width + margin * 2, area.Height + margin * 2);

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileName(path);

        // Pass one: the whole screen, keeping only what sits in the subtitle band.
        var whole = await ReadAsync(image, new System.Windows.Rect(0, 0, image.Width, image.Height));
        var band = whole.Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop).ToList();
        if (band.Count == 0) { Console.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        // The widest line in the band and whatever else belongs to the same subtitle — the second
        // line of a two-line one — and nothing else. The union of the whole band would swallow the
        // interface along the bottom of a game screen, and then the series would be measuring how
        // well a row of buttons reads rather than the subtitle it was pointed at.
        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        band = band.Where(b =>
        {
            var bounds = b.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            var near = Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5;
            return near && overlap > Math.Min(bounds.Width, anchor.Width) * 0.5;
        }).ToList();

        // Pass two: the same text with room around it, which is where this box is most likely to be
        // the whole line rather than part of one.
        var rough = Grow(Union(band, 0, 0), 60);
        var refinedRead = await ReadAsync(image, rough);
        var refined = refinedRead.Count > 0 ? Union(refinedRead, rough.X, rough.Y) : Union(band, 0, 0);

        var readings = new string[margins.Length];
        var chars = new int[margins.Length];
        for (var index = 0; index < margins.Length; index++)
        {
            var blocks = await ReadAsync(image, Grow(refined, margins[index]));
            readings[index] = string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));
            chars[index] = blocks.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));
        }

        var best = chars.Max();
        if (best < MinChars) { Console.WriteLine($"{name}: nothing worth measuring, skipped"); continue; }

        measured++;
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{name}  {image.Width}x{image.Height}  best={best} chars");
        for (var index = 0; index < margins.Length; index++)
        {
            totals[index] += chars[index];
            if (chars[index] == best) perfect[index]++;

            Console.WriteLine(
                $"  +{margins[index],3}px : {chars[index],3}/{best,-3} {readings[index]}");
        }
    }

    if (measured > 0)
    {
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"SUMMARY over {measured} frames");
        var bestTotal = totals.Max();
        for (var index = 0; index < margins.Length; index++)
            Console.WriteLine(
                $"  +{margins[index],3}px : {totals[index],5} chars ({totals[index] * 100.0 / bestTotal,5:0.0}% of best)  " +
                $"read best on {perfect[index],3}/{measured} frames");
    }

    return 0;
}

// The grid issue #89 needs: the same text, at several margins, each read at every detector scale.
//
// --margin-series varies the margin at one size; --scale-sweep varies the size at one margin. Neither
// can separate the variables, and the corpus cannot either — every region dump is about 1820 wide, so
// "fraction" and "absolute detector size" move together in it. Cropping a whole screen at a range of
// margins breaks that tie: the same glyphs, the same fraction, a different absolute size.
//
// What it is for: with a fixed fraction the glyph's height in DETECTOR space is (region glyph height x
// fraction), which does not depend on how the user framed — measured on two real blocks in #39, a
// tight 1435x146 and a loose 1894x269 both put a 55px glyph at ~28px. So glyph scale cannot be what
// makes a tight crop read 88-95% at every fraction while the same frames untrimmed peak at 0.85 and
// fall to 69% at 0.40. Something else moves, and each row here carries the candidates side by side so
// the answer comes from the data rather than from a story about it.
//
// Emitted as CSV on purpose. The other sweeps pad their columns to line up, which is fine to read and
// treacherous to parse — a right-aligned "chars= 78" silently dropped the lowest-scoring rows out of
// an analysis during #84, and those are exactly the rows that matter.
if (args[0] == "--margin-scale-grid")
{
    using var gridOcr = new OcrService();

    const double SubtitleBandTop = 0.62;
    const int MinChars = 8;
    int[] gridMargins = [8, 24, 60, 120, 200];

    System.Windows.Rect Clamp(System.Windows.Rect area, Bitmap source)
    {
        var x = Math.Max(0, area.X);
        var y = Math.Max(0, area.Y);
        return new System.Windows.Rect(
            x, y,
            Math.Min(source.Width - x, area.Width),
            Math.Min(source.Height - y, area.Height));
    }

    async Task<List<OcrTextBlock>> ReadCropAsync(Bitmap source, System.Windows.Rect area, int? size)
    {
        var box = Clamp(area, source);
        if (box.Width <= 1 || box.Height <= 1) return [];

        using var crop = source.Clone(
            new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height), source.PixelFormat);
        return await gridOcr.TryRecognizeAsync(crop, harnessLanguage, size) ?? [];
    }

    static System.Windows.Rect UnionOf(IEnumerable<OcrTextBlock> blocks, double offsetX, double offsetY)
    {
        var union = System.Windows.Rect.Empty;
        foreach (var block in blocks)
        {
            var bounds = block.Bounds;
            bounds.Offset(offsetX, offsetY);
            union.Union(bounds);
        }

        return union;
    }

    static System.Windows.Rect GrowBy(System.Windows.Rect area, int margin) => new(
        area.X - margin, area.Y - margin, area.Width + margin * 2, area.Height + margin * 2);

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    Console.WriteLine(
        "image,margin,roiW,roiH,fraction,detect,chars,glyphRegionPx,glyphDetectorPx,occupancyPct");

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.Error.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileName(path);

        // Locating the subtitle is --margin-series' logic, unchanged: read the whole screen, keep the
        // band low enough to be a subtitle, anchor on the widest line so a row of interface buttons
        // along the bottom does not get swallowed, then re-read with room around it because the first
        // box may already be clipped by the very effect under measurement.
        var whole = await ReadCropAsync(image, new System.Windows.Rect(0, 0, image.Width, image.Height), null);
        var band = whole
            .Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop)
            .ToList();
        if (band.Count == 0) { Console.Error.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        band = band.Where(b =>
        {
            var bounds = b.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            return Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5 &&
                   overlap > Math.Min(bounds.Width, anchor.Width) * 0.5;
        }).ToList();

        var rough = GrowBy(UnionOf(band, 0, 0), 60);
        var refinedRead = await ReadCropAsync(image, rough, null);
        var refined = refinedRead.Count > 0
            ? UnionOf(refinedRead, Math.Max(0, rough.X), Math.Max(0, rough.Y))
            : UnionOf(band, 0, 0);
        if (refined.IsEmpty) { Console.Error.WriteLine($"{name}: no text box, skipped"); continue; }

        // A frame not carrying a subtitle produces a grid of noise moving around, which averages into
        // the summary as though it were signal. Same floor and same reason as --margin-series.
        var refinedChars = refinedRead.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));
        if (refinedChars < MinChars)
        {
            Console.Error.WriteLine($"{name}: only {refinedChars} chars, nothing worth measuring, skipped");
            continue;
        }

        foreach (var margin in gridMargins)
        {
            var roi = Clamp(GrowBy(refined, margin), image);
            if (roi.Width <= 1 || roi.Height <= 1) continue;

            using var crop = image.Clone(
                new Rectangle((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height), image.PixelFormat);
            var native = Math.Max(crop.Width, crop.Height);
            var roiArea = (double)crop.Width * crop.Height;

            for (var percent = 30; percent <= 100; percent += 5)
            {
                var fraction = percent / 100.0;
                var size = fraction >= 1.0
                    ? native
                    : Math.Max(320, ((int)(native * fraction) + 31) / 32 * 32);

                var blocks = await gridOcr.TryRecognizeAsync(crop, harnessLanguage, size) ?? [];

                // The same three drops a watched region applies, so a size counts as reading only
                // what the application would actually have shown.
                var kept = blocks.Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, crop.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence)).ToList();

                var chars = kept.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));

                // SourceGlyphHeight is the ink height and is null for CJK, where the box already is
                // the glyph — taking one or the other rather than both is the #35 mistake, and it is
                // worth a factor of two on Latin.
                var glyphRegion = Median(kept.Select(b => b.SourceGlyphHeight ?? b.Bounds.Height).ToList());
                var glyphDetector = native > 0 ? glyphRegion * size / native : 0;

                var textUnion = UnionOf(kept, 0, 0);
                var occupancy = textUnion.IsEmpty || roiArea <= 0
                    ? 0
                    : textUnion.Width * textUnion.Height * 100.0 / roiArea;

                Console.WriteLine(
                    $"{name},{margin},{crop.Width},{crop.Height},{fraction:0.00},{size},{chars}," +
                    $"{glyphRegion:0.0},{glyphDetector:0.0},{occupancy:0.0}");
            }
        }

        Console.Error.WriteLine($"{name}: done");
    }

    return 0;
}

// Threshold sweep: the same frame read at the shipped detector size, with the three detector
// post-processing thresholds moved one at a time around the library's defaults.
//
// This is the one sweep whose answer cannot be read out of the application's log. rawBlocks is
// counted after the library has already dropped every box below BoxScoreThresh, so a subtitle whose
// leading characters went missing looks identical to one where the detector never found them. The
// only way to tell those apart is to move the threshold and see whether the text comes back.
//
// Reads as a screenshot (detect size null -> the shipped ScreenshotDetectSize) and in AUTO, which is
// what the failing captures were: the point is to reproduce a specific report, not to re-measure the
// realtime sizes that --scale-sweep already covers.
if (args[0] == "--thresh-sweep")
{
    using var threshOcr = new OcrService();

    // What the application actually sends, which since #71 is the model's own exported values
    // rather than the library's generic ones. The library's are kept as a row below so a sweep can
    // still show what moving away from them bought.
    var shipped = OnnxOcrEngine.ExportedThresholds;
    var libraryDefaults = new OnnxOcrEngine.DetectorThresholds(
        RapidOcrNet.RapidOcrOptions.Default.BoxThresh,
        RapidOcrNet.RapidOcrOptions.Default.BoxScoreThresh,
        RapidOcrNet.RapidOcrOptions.Default.UnClipRatio);

    Console.WriteLine(
        $"shipped: boxThresh={shipped.BoxThresh:0.00} boxScore={shipped.BoxScoreThresh:0.00} " +
        $"unclip={shipped.UnClipRatio:0.00}   library: {libraryDefaults.BoxThresh:0.00}/" +
        $"{libraryDefaults.BoxScoreThresh:0.00}/{libraryDefaults.UnClipRatio:0.00}");

    // One axis at a time, each stepping through its own range with the other two left shipped. A
    // grid would be three times the passes for an answer nobody could read: what is being asked
    // first is which of the three is even involved.
    // The values PaddlePaddle exported each v6 detector with, from the inference.yml beside the
    // model on Hugging Face. They are here as whole combinations because that is what they are: this
    // sweep otherwise moves one axis at a time and holds the other two at the library's generic
    // defaults, which cannot find a setting whose three parts only work together. #69 swept all
    // three that way and concluded none of them mattered.
    var runs = new List<(string Axis, OnnxOcrEngine.DetectorThresholds Value)>
    {
        ("shipped        ", shipped),
        ("library-default", libraryDefaults),
        ("official-sm/med", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.45f, 1.4f)),

        // Neither the library's defaults nor the export config are aimed at what goes wrong here.
        // Both move the three together in the same direction — permissive or strict — while the
        // three faults on record want opposite things: a low binarisation threshold to keep the
        // faint leading strokes a line loses, a HIGH box score to throw away the noise boxes that
        // reach the screen as enormous garbage, and a low unclip so a box does not come out taller
        // than its glyphs. PaddleOCR's own tuning guidance names those three moves separately; no
        // published preset combines them, so these are the combinations to measure.
        ("weak+strict-1.4", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.4f)),
        ("weak+strict-1.5", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.5f)),
        ("weak+mid-1.4   ", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.5f, 1.4f)),
        ("weak+strict-1.6", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.6f)),

        // One move at a time from what ships now, so the combinations above can be read against
        // which single change was doing the work.
        ("only-weaker    ", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.5f, 1.6f)),
        ("only-tighter   ", new OnnxOcrEngine.DetectorThresholds(0.3f, 0.5f, 1.4f)),
        ("only-stricter  ", new OnnxOcrEngine.DetectorThresholds(0.3f, 0.6f, 1.6f)),
    };
    foreach (var boxThresh in new[] { 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.40f, 0.50f })
        runs.Add(($"boxThresh={boxThresh:0.00}", shipped with { BoxThresh = boxThresh }));
    foreach (var boxScore in new[] { 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f })
        runs.Add(($"boxScore ={boxScore:0.00}", shipped with { BoxScoreThresh = boxScore }));

    // Finer than the other two and reaching lower, because #71 needs this one to answer a question
    // about box size rather than about whether text is found: the unclip step is what inflates a
    // detected polygon back out, and a detector whose boxes come out too tall is asking to be tried
    // below the library's 1.6.
    foreach (var unclip in new[] { 0.8f, 1.0f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.5f })
        runs.Add(($"unclip   ={unclip:0.00}", shipped with { UnClipRatio = unclip }));

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  " +
            $"detect={harnessSize?.ToString() ?? "screenshot"}  lang={harnessLanguage}");

        foreach (var (axis, thresholds) in runs)
        {
            OnnxOcrEngine.DetectorThresholdOverride = thresholds;

            List<OcrTextBlock>? blocks = null;
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
                blocks = await threshOcr.TryRecognizeAsync(image, harnessLanguage, harnessSize);

            var chars = blocks?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var text = blocks is null || blocks.Count == 0
                ? ""
                : "  " + string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));

            // The tallest box, because #71 is about box shape rather than about what was read: a
            // reading that is right and a box that is twice the height of its glyphs still reaches
            // the screen as text twice the size it should be, and the character count cannot see it.
            var tallest = blocks is null || blocks.Count == 0 ? 0 : blocks.Max(b => b.Bounds.Height);

            Console.WriteLine(
                $"  {axis} : {blocks?.Count ?? -1} box chars={chars,3} tallest={tallest,3:0}{text}");
        }

        OnnxOcrEngine.DetectorThresholdOverride = null;
    }

    return 0;
}

// Padding sweep: the same frame at the size the application would really use, read with a range of
// borders around it. The border exists because text touching the edge of a capture detects badly,
// but it is not free — it counts towards the long side ImgResize caps, so a wider border shrinks
// the text the detector sees. This says whether the shipped 50 is still the right number.
if (args[0] == "--pad-sweep")
{
    using var padOcr = new OcrService();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Path.GetFileName(path)}  {image.Width}x{image.Height}  detect={primary}");

        foreach (var padding in new[] { 0, 8, 16, 24, 32, 50, 64, 96 })
        {
            OnnxOcrEngine.DetectorPaddingOverride = padding;

            List<OcrTextBlock>? blocks = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
            {
                elapsed.Restart();
                blocks = await padOcr.TryRecognizeAsync(image, "EN", primary);
            }

            elapsed.Stop();

            // The same two filters the realtime session applies, for the same reason as in the
            // scale sweep: a reading it would have thrown away is not a reading.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList();

            var chars = kept?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var text = kept is null || kept.Count == 0
                ? ""
                : "  " + string.Join(" | ", kept.Select(b => b.Text.Replace("\n", " ")));
            var mark = padding == 50 ? " <- shipped" : "";

            Console.WriteLine(
                $"  pad={padding,3} : {kept?.Count ?? -1} box chars={chars,3} " +
                $"{elapsed.ElapsedMilliseconds,5}ms{mark}{text}");
        }

        OnnxOcrEngine.DetectorPaddingOverride = null;
    }

    return 0;
}

// Margin sweep: the same text read inside blocks drawn tight around it and drawn loosely around it.
//
// Every other sweep here holds the block still and changes what is done to it. This one changes the
// block, because that is the one input the user chooses and the only one nobody has measured: a
// dumped frame is cropped back to the text plus a margin of so many line heights, and each crop is
// read exactly as the app would read a block of that shape — RealtimeDetectorSize picks the size
// from the cropped dimensions, and the realtime filters throw away what the session would throw
// away.
//
// It can only ever take margin away, never add it, since the pixels outside the dumped block were
// never captured. So the loosest row is the block as the user actually drew it, and the rows below
// it are that same content framed tighter.
if (args[0] == "--margin-sweep")
{
    using var marginOcr = new OcrService();

    // Fractions of a line's height to leave around the text on all four sides.
    double[] margins = [0, 0.15, 0.3, 0.5, 0.75, 1.0, 1.5];
    var totals = new double[margins.Length];
    var counted = new int[margins.Length];

    async Task<List<OcrTextBlock>> ReadAsync(Bitmap image)
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

        foreach (var size in new[] { primary }.Concat(fallbacks))
        {
            var blocks = await marginOcr.TryRecognizeAsync(image, "EN", size);

            // The two filters the realtime session applies. A reading it would have thrown away is
            // not a reading, and the collapse filter is measured against the block's own height —
            // which is exactly what this sweep is changing.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList() ?? [];

            if (kept.Count > 0) return kept;
        }

        return [];
    }

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);

        // Where the text is, read from the block as drawn. A frame nothing can be read out of has
        // no text to centre a crop on, and counting it would score cropping against a frame that
        // was never readable in the first place.
        var located = await ReadAsync(image);
        if (located.Count == 0) continue;

        var lineHeight = located.Select(block => block.Bounds.Height).OrderBy(h => h).ToList()
            [located.Count / 2];
        var left = located.Min(block => block.Bounds.Left);
        var top = located.Min(block => block.Bounds.Top);
        var right = located.Max(block => block.Bounds.Right);
        var bottom = located.Max(block => block.Bounds.Bottom);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  " +
            $"line={lineHeight:0}px  text={right - left:0}x{bottom - top:0}");

        var readings = new (double Margin, string Size, int Boxes, int Chars, long Ms, string Text)[margins.Length];

        for (var i = 0; i < margins.Length; i++)
        {
            var pad = margins[i] * lineHeight;
            var crop = Rectangle.FromLTRB(
                Math.Max(0, (int)Math.Floor(left - pad)),
                Math.Max(0, (int)Math.Floor(top - pad)),
                Math.Min(image.Width, (int)Math.Ceiling(right + pad)),
                Math.Min(image.Height, (int)Math.Ceiling(bottom + pad)));

            using var cropped = image.Clone(crop, image.PixelFormat);
            var (primary, _) = RealtimeDetectorSize.For(cropped.Width, cropped.Height, harnessMode);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var kept = await ReadAsync(cropped);
            elapsed.Stop();

            var chars = kept.Sum(block => block.Text.Count(c => !char.IsWhiteSpace(c)));
            readings[i] = (
                margins[i],
                $"{cropped.Width}x{cropped.Height} d={primary}",
                kept.Count,
                chars,
                elapsed.ElapsedMilliseconds,
                string.Join(" | ", kept.Select(block => block.Text.Replace("\n", " "))));
        }

        // Scored against the frame's own best reading rather than against ground truth, the same way
        // the pad sweep is: what is being compared is one framing of one frame against another.
        var best = readings.Max(reading => reading.Chars);
        if (best == 0) continue;

        for (var i = 0; i < readings.Length; i++)
        {
            var reading = readings[i];
            var share = (double)reading.Chars / best;
            totals[i] += share;
            counted[i]++;

            Console.WriteLine(
                $"  margin={reading.Margin:0.00}line {reading.Size,-18} {reading.Boxes} box " +
                $"chars={reading.Chars,3} {share,5:0%} {reading.Ms,4}ms  {reading.Text}");
        }
    }

    Console.WriteLine(new string('=', 78));
    Console.WriteLine("share of each frame's own best reading, by how much margin the block left:");
    for (var i = 0; i < margins.Length; i++)
        if (counted[i] > 0)
            Console.WriteLine(
                $"  margin={margins[i]:0.00} line heights : {totals[i] / counted[i],6:0.0%}  " +
                $"({counted[i]} frames)");

    return 0;
}

// Scale sweep: the same frame read at every detector size, to see which sizes find the text and
// how wide the working band is. This is the measurement #22 step 0 asks for, and it only means
// anything on frames whose contents are known — the dumped "rescued" frames, where the primary
// size read nothing and a fallback read the subtitle fine.
// What the confidence filter throws away, and whether any of it belonged to a real line.
//
// Issue #85: RejectUnconvincingBlocks runs BEFORE OcrTextBlockGrouper, so a low-confidence tail
// fragment is gone before MergeSameLineFragments could join it back onto the sentence it came from
// — the subtitle then reaches the screen a few characters short. Swapping the two is not available;
// OcrService records why (a scenery box merged into a subtitle takes the subtitle down with it when
// the merged box is judged a collapse).
//
// So the question is whether a narrower rule is worth writing, and that turns on WHAT such a rule
// would let back in. This mode answers exactly that: for every fragment the filter drops, it runs
// the real grouper over the UNFILTERED blocks and reports whether the fragment would have landed
// inside a line long enough to be real. Those are the readings a carve-out would keep; everything
// else is what it would still drop. Read the two lists before writing the rule, not after.
if (args[0] == "--reject-audit")
{
    using var auditEngine = new OnnxOcrEngine();
    int framesRead = 0, framesLosing = 0, droppedTotal = 0, wouldMerge = 0, isolated = 0;

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var size = harnessSize
            ?? RealtimeDetectorSize.For(image.Width, image.Height, harnessMode).Primary;

        // The engine directly, not OcrService: OcrService is where the filter lives, and these are
        // the blocks as they exist before it runs.
        var raw = await auditEngine.TryRecognizeAsync(image, harnessLanguage, size);
        if (raw is null || raw.Count == 0) continue;

        framesRead++;

        var dropped = raw
            .Where(block => ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
            .ToList();
        if (dropped.Count == 0) continue;

        framesLosing++;
        droppedTotal += dropped.Count;

        var groupedUnfiltered = OcrTextBlockGrouper.Group(raw);

        foreach (var block in dropped)
        {
            var fragment = block.Text.Trim();

            // Whatever the grouper merged this fragment into is what it would have become. Long
            // enough to be real is the same floor the filter itself uses, so the two rules are
            // being compared on one scale rather than two.
            var host = groupedUnfiltered.FirstOrDefault(group =>
                group.Text.Contains(fragment, StringComparison.Ordinal) &&
                group.Text.Trim().Length > fragment.Length &&
                group.Text.Trim().Length >= ShortReadingDetection.ShortTextLength);

            if (host is null) { isolated++; continue; }

            wouldMerge++;
            // The fragment itself, because the judgement this mode exists to support cannot be made
            // from a count: a 3-character reading is either the end of a word or a scrap of scenery,
            // and only looking at it says which.
            Console.WriteLine(
                $"  MERGES  {Path.GetFileName(path)} @{size}  conf={block.Confidence:0.00}  " +
                $"\"{fragment}\" -> line of {host.Text.Trim().Length}ch");
        }
    }

    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"frames read             : {framesRead}");
    Console.WriteLine($"frames losing a fragment: {framesLosing}");
    Console.WriteLine($"fragments dropped       : {droppedTotal}");
    Console.WriteLine($"  would have merged     : {wouldMerge}   <- what a narrowed rule would keep");
    Console.WriteLine($"  isolated              : {isolated}   <- what it would still drop");
    return 0;
}

// Every next-line verdict the grouper reached, with the geometry it judged on.
//
// The ordinary output shows which lines were joined and no more, so a paragraph that came back as
// four separate translations says nothing about which threshold refused it, or by how much. These
// thresholds are the whole of the grouper and they are tuned against real captures, so seeing the
// near misses is what makes tuning something other than guesswork: a run of pairs refused on
// "vertical gap" at 0.83 of a line is a different problem from one refused on "no continuation
// evidence" with the gap at 0.2.
//
// Numbers are in line heights, not pixels, so captures of different sizes can be read side by side.
if (args[0] == "--group-explain")
{
    using var explainEngine = new OnnxOcrEngine();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"IMAGE: {path}");

        using var image = new Bitmap(path);
        var size = harnessSize
            ?? RealtimeDetectorSize.For(image.Width, image.Height, harnessMode).Primary;
        var raw = await explainEngine.TryRecognizeAsync(image, harnessLanguage, size);
        if (raw is null || raw.Count == 0) { Console.WriteLine("  (nothing read)"); continue; }

        var trace = new OcrTextBlockGrouper.GroupTrace();
        var grouped = OcrTextBlockGrouper.Group(raw, trace);

        Console.WriteLine($"  lines read: {raw.Count}  ->  groups sent to translation: {grouped.Count}");
        for (var i = 0; i < grouped.Count; i++)
            Console.WriteLine($"  [{i}] lines={grouped[i].Lines.Count}  {grouped[i].Text}");

        // What the same-row threshold came out as, before the verdicts it decided. Reading the
        // verdicts without it means reading "SPLIT at 0.52" with no way to tell whether 0.52 was
        // close to the line or nowhere near it.
        var threshold = trace.SameLineThreshold;
        Console.WriteLine(
            $"  --- same-row gap threshold: {threshold.Value:0.000} " +
            $"({(threshold.Adaptive ? "adaptive" : "fallback")}: {threshold.Reason}) ---");
        // Only the gaps the estimate actually looks at. The rest are the distances across a
        // layout rather than between neighbours, and printing forty of them buries the ten that
        // decided the threshold.
        var sampled = trace.SameLineGaps.Where(gap => gap is >= -1.0 and <= 2.0).OrderBy(gap => gap).ToList();
        Console.WriteLine(
            $"      {sampled.Count} of {trace.SameLineGaps.Count} gaps in range: " +
            (sampled.Count == 0 ? "(none)" : string.Join(" ", sampled.Select(gap => $"{gap:0.00}"))));

        // Rows first, then lines, because that is the order they ran in: boxes are gathered into
        // lines before any line is asked whether it continues another.
        Console.WriteLine("  --- merge verdicts (gap/fit in line heights) ---");
        foreach (var decision in trace.Decisions.OrderByDescending(decision => decision.Kind))
        {
            Console.WriteLine(
                $"  {decision.Kind,-4} {(decision.Joined ? "JOIN  " : "SPLIT ")} " +
                $"gap={decision.Gap,6:0.00} fit={decision.Fit,6:0.00} " +
                $"size={decision.TextSizeRatio:0.00} width={decision.WidthRatio:0.00}  [{decision.Rule}]");
            Console.WriteLine($"      \"{Shorten(decision.Previous)}\" + \"{Shorten(decision.Current)}\"");
        }
    }

    return 0;

    static string Shorten(string text) =>
        text.Length <= 42 ? text : string.Concat(text.AsSpan(0, 40), "…");
}

if (args[0] == "--scale-sweep")
{
    var sweepArgs = args.Skip(1).ToList();

    if (OnnxOcrEngine.DetectorOverride is null)
        Console.WriteLine("detector: shipped (ocrmodels/onnx/shared/det.onnx)");

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
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);
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

            // A watched region does not use everything the engine hands back: a box as tall as the
            // block is the detector having collapsed, and a one-character reading is scenery. Both
            // are dropped in RealtimeTranslationSession, after OcrService, so a sweep that skips
            // them reports sizes as working that the application would have called empty — measured
            // on the shipped detector, five of these frames read their subtitle at the very size the
            // application had recorded as reading nothing.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList();

            var mark = size == primary ? " <- primary" : fallbacks.Contains(size) ? " <- fallback" : "";
            var text = kept is null || kept.Count == 0
                ? ""
                : "  " + string.Join(" | ", kept.Select(b => b.Text.Replace("\n", " ")));

            // chars and ms, because neither question this sweep exists for can be answered without
            // both: a size is only counted as reading the subtitle past a character floor (icons and
            // scenery misreads come back short), and the size that reads most is not the size to pick
            // if it costs several times as much per pass.
            var chars = kept?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var dropped = (blocks?.Count ?? 0) - (kept?.Count ?? 0);

            Console.WriteLine(
                $"  {fraction:0.00} -> {size,5} : {kept?.Count ?? -1} box dropped={dropped} chars={chars,3} " +
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

        // What actually reaches the screen. A group is one request and several bubbles: it goes up
        // whole so the translator has the sentence, and comes back onto the lines it was read from
        // so the capture keeps its layout. Printed separately from the request above because the
        // two counts differing is the point, not a discrepancy.
        var placed = GroupedTranslationLines.SplitOntoSourceLines(translated);
        Console.WriteLine($"  --- placed on {placed.Count} source lines ---");
        foreach (var p in placed)
            Console.WriteLine(
                $"  ({p.Bounds.X:0},{p.Bounds.Y:0},{p.Bounds.Width:0},{p.Bounds.Height:0}) {p.TranslatedText}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  TRANSLATE FAILED: {ex.Message}");
    }
}

return 0;
