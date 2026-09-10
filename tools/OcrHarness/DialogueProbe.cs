using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

internal static class DialogueProbe
{
    internal static async Task<int> Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("--dialogue-probe output.json images... (or cached input.json)");
        var captures = new List<GroupingPrototype.Capture>();
        if (args.Length == 2 && args[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            captures.AddRange(JsonSerializer.Deserialize<GroupingPrototype.Capture[]>(File.ReadAllText(args[1]))!);
        else
        {
            using var engine = new OnnxOcrEngine();
            var entries = args.Skip(1).SelectMany(arg => arg.StartsWith('@') ? File.ReadAllLines(arg[1..]) : [arg]);
            foreach (var entry in entries.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var fields = entry.Split('\t');
                var path = fields[0];
                using var bitmap = new Bitmap(path);
                var language = fields.Length > 1 ? fields[1] : path.Contains("-ja") ? "JA" : "EN";
                var size = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, RealtimeBlockMode.Subtitle).Primary;
                List<OcrTextBlock> raw = [];
                for (int pass = 0; pass < 3; pass++)
                {
                    var timer = Stopwatch.StartNew();
                    raw = await engine.TryRecognizeAsync(bitmap, language, size) ?? throw new InvalidOperationException("No OCR slot");
                    Console.WriteLine($"OCR {Path.GetFileName(path)} pass={pass} size={size} ms={timer.Elapsed.TotalMilliseconds:F2} blocks={raw.Count}");
                }
                captures.Add(new(path, "realtime", language, raw.Select(GroupingPrototype.Block.From).ToArray()));
            }
        }
        var results = new List<object>();
        foreach (var capture in captures)
        {
            var raw = capture.Blocks.Select(b => b.Restore()).ToList();
            using var bitmap = new Bitmap(capture.Image);
            foreach (var mode in new[] { RealtimeBlockMode.Panel, RealtimeBlockMode.Subtitle })
            {
                var label = mode == RealtimeBlockMode.Panel ? "legacy-realtime" : "dialogue";
                List<OcrTextBlock> Finish(List<OcrTextBlock> b) => RealtimeTranslationSession.RejectShortReadings(
                    RealtimeTranslationSession.RejectCollapsedBlocks(b, bitmap.Height, -1), -1);
                List<OcrTextBlock> Group() => OcrService.GroupRealtime(raw, bitmap.Height, mode);
                for (int i = 0; i < 100; i++) Group();
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++) Group();
                var us = timer.Elapsed.TotalMicroseconds / 2000;
                var trace = new GroupingTrace();
                var grouped = OcrService.GroupRealtime(raw, bitmap.Height, mode, trace);
                var sources = trace.Lines.ToDictionary(l => l.Id, l => l.SourceIds);
                var ids = trace.Blocks.ToDictionary(b => b.Id, b => Array.FindIndex(raw.ToArray(), x => ReferenceEquals(x, b.Block)));
                var kept = Finish(grouped);
                var groups = trace.Groups.Where((g, i) => kept.Contains(grouped[i])).Select(g => g.SelectMany(l => sources[l]).Select(id => ids[id]).ToArray()).ToArray();
                grouped = kept;
                Console.WriteLine($"GROUP {label} {Path.GetFileName(capture.Image)} blocks={raw.Count} groups={grouped.Count} us={us:F2}");
                results.Add(new { capture.Image, Mode = label, Microseconds = us, Groups = groups, Output = grouped.Select(GroupingPrototype.Block.From).ToArray() });
            }
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        File.WriteAllText(args[0], JsonSerializer.Serialize(results, options).ReplaceLineEndings("\r\n"));
        if (!args[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(Path.ChangeExtension(args[0], ".inputs.json"), JsonSerializer.Serialize(captures, options).ReplaceLineEndings("\r\n"));
        return 0;
    }
}
