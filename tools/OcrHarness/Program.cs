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
    Console.Error.WriteLine("       OcrHarness --thresh-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame, detector box thresholds moved one axis at a time)");
    Console.Error.WriteLine("       OcrHarness --pad-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame and size, a range of borders around it)");
    Console.Error.WriteLine("       OcrHarness --margin-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same text, blocks cropped tight around it vs left loose)");
    Console.Error.WriteLine("       OcrHarness --xlate-test   (network translation/resilience check, no OCR)");
    Console.Error.WriteLine("       (add --panel to any sweep to read as a game panel rather than a subtitle)");
    return 1;
}

// Which mode to read as. The application asks the user this (see RealtimeBlockMode) because the
// answer is about what is on screen and not about the picture's shape; offline there is nobody to
// ask, so it is a flag. Subtitle by default: every dump kept so far is one, and reading a subtitle
// dump as a panel would report the wrong half of RealtimeDetectorSize.
var harnessMode = args.Contains("--panel") ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
args = [.. args.Where(argument => argument != "--panel")];

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

    var shipped = new OnnxOcrEngine.DetectorThresholds(
        RapidOcrNet.RapidOcrOptions.Default.BoxThresh,
        RapidOcrNet.RapidOcrOptions.Default.BoxScoreThresh,
        RapidOcrNet.RapidOcrOptions.Default.UnClipRatio);

    Console.WriteLine(
        $"library defaults: boxThresh={shipped.BoxThresh:0.00} " +
        $"boxScore={shipped.BoxScoreThresh:0.00} unclip={shipped.UnClipRatio:0.00}");

    // One axis at a time, each stepping through its own range with the other two left shipped. A
    // grid would be three times the passes for an answer nobody could read: what is being asked
    // first is which of the three is even involved.
    var runs = new List<(string Axis, OnnxOcrEngine.DetectorThresholds Value)>();
    foreach (var boxThresh in new[] { 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.40f, 0.50f })
        runs.Add(($"boxThresh={boxThresh:0.00}", shipped with { BoxThresh = boxThresh }));
    foreach (var boxScore in new[] { 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f })
        runs.Add(($"boxScore ={boxScore:0.00}", shipped with { BoxScoreThresh = boxScore }));
    foreach (var unclip in new[] { 1.5f, 1.75f, 2.0f, 2.25f, 2.5f, 3.0f })
        runs.Add(($"unclip   ={unclip:0.00}", shipped with { UnClipRatio = unclip }));

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Path.GetFileName(path)}  {image.Width}x{image.Height}  detect=screenshot");

        foreach (var (axis, thresholds) in runs)
        {
            OnnxOcrEngine.DetectorThresholdOverride = thresholds;

            List<OcrTextBlock>? blocks = null;
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
                blocks = await threshOcr.TryRecognizeAsync(image, "AUTO");

            var chars = blocks?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var text = blocks is null || blocks.Count == 0
                ? ""
                : "  " + string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));
            var mark = thresholds == shipped ? " <- shipped" : "";

            Console.WriteLine($"  {axis} : {blocks?.Count ?? -1} box chars={chars,3}{mark}{text}");
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
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  TRANSLATE FAILED: {ex.Message}");
    }
}

return 0;
