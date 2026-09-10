using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// Detector input geometry A/B.
//
// The shipped path hands the library an aligned bitmap and asks it to resize to ImgResize. The
// library's ScaleParam computes an aspect-preserving target and then quantises EACH AXIS
// INDEPENDENTLY with (n / 32 - 1) * 32 whenever that axis is not already a multiple of 32 — a full
// stride below flooring. AlignForDetector only neutralises that when the resulting ratio is exactly
// one, which needs ImgResize >= the aligned long side; the app computes ImgResize from the size
// BEFORE alignment, so the ratio lands just under one and the quantisation runs anyway.
//
// The candidate does the downscale itself, isotropically, then aligns, then tells the library not
// to resize at all (ImgResize far above the input). Prepared dimensions are then stride multiples
// at ratio one, so the quantisation is skipped and the detector sees the aspect ratio it was
// handed. Everything after the raw blocks is the app's own chain.
internal static class DetectorGeometryProbe
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static int Run(string[] args)
    {
        string output = args[0];
        Directory.CreateDirectory(output);
        string language = "JA";
        var mode = RealtimeBlockMode.Subtitle;
        double[] scales = [1.0];
        int[] pads = [0];
        int[] anisoPads = [];
        // What the stride-alignment strip on the right and bottom is filled with. The shipped path
        // leaves it transparent and lets the library decide: composited white when a border runs,
        // premultiplied black when Padding is 0. Naming it makes that an input rather than a leak.
        string[] fills = ["clear"];
        // The screenshot flow instead of the realtime one: no fraction (ScreenshotDetectSize), and
        // the screenshot side's own grouping rather than GroupRealtime.
        bool screenshot = false;
        var profile = GroupingProfile.General;
        var images = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang": language = args[++i]; break;
                case "--mode": mode = Enum.Parse<RealtimeBlockMode>(args[++i], true); break;
                case "--pads": pads = args[++i].Split(',').Select(int.Parse).ToArray(); break;
                case "--aniso-pads": anisoPads = args[++i].Split(',').Select(int.Parse).ToArray(); break;
                case "--fills": fills = args[++i].Split(','); break;
                case "--screenshot": screenshot = true; break;
                case "--interface": profile = GroupingProfile.Interface; break;
                case "--scales":
                    scales = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    break;
                default: images.Add(args[i]); break;
            }
        }

        var root = Path.Combine(AppContext.BaseDirectory, "ocrmodels", "onnx");
        string modelKey = OnnxOcrEngine.GetModelKeyForLanguage(language);
        using var engine = new RapidOcr();
        engine.InitModels(new RapidOcrModelSet
        {
            DetModelPath = Path.Combine(root, "shared", "det.onnx"),
            ClsModelPath = Path.Combine(root, "shared", "cls.onnx"),
            RecModelPath = Path.Combine(root, modelKey, "rec.onnx"),
            KeysPath = Path.Combine(root, modelKey, "dict.txt"),
            DetMean = [127.5f, 127.5f, 127.5f],
            DetStd = [127.5f, 127.5f, 127.5f],
        }, 2);

        var prep = typeof(RapidOcr).GetMethod("PrepareDetectorInput", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        bool cjk = OcrLanguageRouter.UsesCjkOnnx(language);
        var rows = new List<object>();

        foreach (string path in images)
        {
            using var source = SKBitmap.Decode(path);
            if (source is null)
            {
                Console.WriteLine("SKIP " + path);
                continue;
            }

            int? requested = screenshot
                ? null
                : RealtimeDetectorSize.For(source.Width, source.Height, mode).Primary;
            var shipped = OnnxOcrEngine.CreateOptions(requested);
            int primary = shipped.ImgResize;

            // "shipped" is whatever the product does today, reached through the product's own
            // builder. "legacy" reproduces the pre-fix pipeline — the library's 50px border and its
            // per-axis quantised resize — so a run can still show what the distortion cost.
            var variants = new List<(string Name, int Target, RapidOcrOptions Options, string Fill)>
            {
                ("shipped", 0, shipped, "clear"),
                ("legacy", -1, shipped with { Padding = 50 }, "clear"),
            };
            // The other half of the 2x2: the border changed, the library's own resize left alone,
            // so a border effect can be told apart from the geometry it silently drags with it.
            foreach (int pad in anisoPads)
                variants.Add(("aniso-p" + pad, -1, shipped with { Padding = pad }, "clear"));

            foreach (double scale in scales)
            foreach (int pad in pads)
            foreach (string fill in fills)
            {
                int target = Math.Max(32, (int)Math.Round(primary * scale));
                variants.Add((
                    "iso-" + scale.ToString("0.##", CultureInfo.InvariantCulture) + "-p" + pad
                        + (fill == "clear" ? "" : "-" + fill),
                    target,
                    shipped with { ImgResize = 8192, Padding = pad },
                    fill));
            }

            foreach (var variant in variants)
            {
                bool product = variant.Name == "shipped";
                bool ownResize = variant.Target > 0;
                using var scaled = ownResize ? IsoResize(source, variant.Target) : source.Copy();
                using var frame = product ? OnnxOcrEngine.CreateDetectorFrame(source, requested) : default;
                double rx = product ? frame.RatioX : (double)scaled.Width / source.Width;
                double ry = product ? frame.RatioY : (double)scaled.Height / source.Height;
                var options = product ? frame.Options : variant.Options;

                using var owned = product
                    ? null
                    : variant.Fill == "clear"
                        ? OnnxOcrEngine.AlignForDetector(scaled, variant.Options.Padding)
                        : AlignFilled(scaled, variant.Options.Padding, variant.Fill);
                var input = product ? frame.Bitmap : owned!;
                int preparedWidth, preparedHeight, dstWidth, dstHeight;
                using (var prepared = (IDisposable)prep.Invoke(null, [input, options])!)
                {
                    var bitmap = (SKBitmap)prepared.GetType().GetField("Bitmap", Hidden)!.GetValue(prepared)!;
                    var scaleParam = prepared.GetType().GetField("Scale", Hidden)!.GetValue(prepared)!;
                    preparedWidth = bitmap.Width;
                    preparedHeight = bitmap.Height;
                    dstWidth = (int)scaleParam.GetType().GetProperty("DstWidth", Hidden)!.GetValue(scaleParam)!;
                    dstHeight = (int)scaleParam.GetType().GetProperty("DstHeight", Hidden)!.GetValue(scaleParam)!;
                }

                string[] finalText = [];
                double milliseconds = 0;
                object[] raw = [];
                for (int pass = 0; pass < 2; pass++)
                {
                    var watch = Stopwatch.StartNew();
                    var result = engine.Detect(input, options);
                    milliseconds = watch.Elapsed.TotalMilliseconds;

                    var mapped = result.TextBlocks.Select(b => new TextBlock
                    {
                        Text = b.Text,
                        Chars = b.Chars,
                        CharScores = b.CharScores,
                        BoxScore = b.BoxScore,
                        BoxPoints = b.BoxPoints
                            .Select(p => new SKPointI((int)Math.Round(p.X / rx), (int)Math.Round(p.Y / ry)))
                            .ToArray(),
                    }).ToArray();

                    var kept = OnnxOcrEngine.ApplyBlockFilters(
                        mapped, language, cjk, OcrLanguageRouter.UsesAutomaticLayout(language));
                    List<OcrTextBlock> grouped;
                    if (screenshot)
                    {
                        using var drawing = new System.Drawing.Bitmap(path);
                        grouped = OcrTextBlockGrouper.Group(
                            OcrService.PrepareScreenshotGrouping(drawing, kept, profile), profile);
                    }
                    else
                    {
                        grouped = RealtimeTranslationSession.RejectShortReadings(
                            RealtimeTranslationSession.RejectCollapsedBlocks(
                                OcrService.GroupRealtime(kept, source.Height, mode), source.Height, -1), -1);
                    }

                    finalText = grouped.Select(b => b.Text).ToArray();
                    raw = result.TextBlocks.Select(b => (object)new
                    {
                        b.Text,
                        Score = b.CharScores is { Length: > 0 } s ? s.Average() : 0f,
                        Points = b.BoxPoints.Select(p => new[] { (int)Math.Round(p.X / rx), (int)Math.Round(p.Y / ry) }),
                    }).ToArray();
                }

                rows.Add(new
                {
                    Image = path,
                    variant.Name,
                    Language = language,
                    Mode = mode.ToString(),
                    Primary = primary,
                    Target = variant.Target,
                    SourceWidth = source.Width,
                    SourceHeight = source.Height,
                    ScaledWidth = scaled.Width,
                    ScaledHeight = scaled.Height,
                    PreparedWidth = preparedWidth,
                    PreparedHeight = preparedHeight,
                    DstWidth = dstWidth,
                    DstHeight = dstHeight,
                    NetworkScaleX = Math.Round((double)dstWidth / preparedWidth * rx, 4),
                    NetworkScaleY = Math.Round((double)dstHeight / preparedHeight * ry, 4),
                    Milliseconds = Math.Round(milliseconds, 1),
                    Raw = raw,
                    Final = finalText,
                });

                Console.WriteLine(
                    $"GEO {Path.GetFileName(path)} {variant.Name} {milliseconds:F0}ms " +
                    $"sx={(double)dstWidth / preparedWidth * rx:F3} sy={(double)dstHeight / preparedHeight * ry:F3}: " +
                    string.Join(" | ", finalText));
            }
        }

        File.WriteAllText(
            Path.Combine(output, "geometry.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })
                .ReplaceLineEndings("\r\n"));
        return 0;
    }

    // AlignForDetector with the strip painted rather than left to whatever the library composites
    // it against.
    private static SKBitmap AlignFilled(SKBitmap source, int pad, string fill)
    {
        using var clear = OnnxOcrEngine.AlignForDetector(source, pad);
        var filled = new SKBitmap(clear.Width, clear.Height, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(filled))
        {
            canvas.Clear(fill == "black" ? SKColors.Black : SKColors.White);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0);
        }

        return filled;
    }

    // Same sampler the library resizes with, so the comparison is about geometry and not about
    // which filter drew the glyphs.
    private static SKBitmap IsoResize(SKBitmap source, int target)
    {
        double ratio = Math.Min(1.0, (double)target / Math.Max(source.Width, source.Height));
        if (ratio >= 1.0)
            return source.Copy();

        int width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var resized = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return resized;
    }
}
