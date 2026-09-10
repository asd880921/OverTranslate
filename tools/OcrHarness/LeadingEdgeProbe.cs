using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// Diagnostic only: one-axis comparisons, no candidate is selected or installed by this probe.
internal static class LeadingEdgeProbe
{
    internal static async Task<int> Run(string[] args, bool controls = false)
    {
        if (args.Length < 2) throw new ArgumentException("--leading-edge-probe output.json images...");
        using var engine = new OnnxOcrEngine();
        var results = new List<object>();
        try
        {
            var entries = args.Skip(1).SelectMany(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<GroupingPrototype.Capture[]>(File.ReadAllText(p))!.Select(c => (c.Image, c.Language))
                : new[] { (Image: p, Language: "JA") });
            foreach (var entry in entries)
            {
                var path = entry.Image;
                using var bitmap = new Bitmap(path);
                var primary = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, RealtimeBlockMode.Subtitle).Primary;
                var variants = new List<(string Name, int Size, int? Padding)> { ("baseline", primary, null) };
                variants.AddRange((controls ? new[] { 16, 24 } : new[] { 0, 8, 16, 24, 32, 64, 96 }).Select(p => ($"pad-{p}", primary, (int?)p)));
                if (!controls) variants.AddRange(new[] { 0.5, 0.65, 0.75, 0.95, 1.0 }.Select(f =>
                    ($"scale-{f}", Math.Max(32, (int)Math.Round(Math.Max(bitmap.Width, bitmap.Height) * f / 32) * 32), (int?)null)));
                foreach (var variant in variants)
                {
                    OnnxOcrEngine.DetectorPaddingOverride = variant.Padding;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        var clock = Stopwatch.StartNew();
                        var raw = await engine.TryRecognizeAsync(bitmap, entry.Language, variant.Size) ?? throw new InvalidOperationException("No OCR slot");
                        var ms = clock.Elapsed.TotalMilliseconds;
                        var grouped = RealtimeTranslationSession.RejectShortReadings(
                            RealtimeTranslationSession.RejectCollapsedBlocks(
                                OcrService.GroupRealtime(raw, bitmap.Height, RealtimeBlockMode.Subtitle), bitmap.Height, -1), -1);
                        results.Add(new { Image = path, entry.Language, variant.Name, variant.Size, variant.Padding, Pass = pass, Milliseconds = ms,
                            Raw = raw.Select(GroupingPrototype.Block.From).ToArray(), Output = grouped.Select(GroupingPrototype.Block.From).ToArray() });
                        Console.WriteLine($"EDGE {Path.GetFileName(path)} {variant.Name} pass={pass} ms={ms:F1}: {string.Join(" | ", grouped.Select(b => b.Text))}");
                    }
                }
            }
        }
        finally { OnnxOcrEngine.DetectorPaddingOverride = null; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\r\n"));
        return 0;
    }
}
