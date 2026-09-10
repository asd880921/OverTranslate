using System.IO;
using System.Text.Json;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

// Offline hypothesis only. Never changes engine settings or production readings.
internal static class LeadingEdgeFusionProbe
{
    internal record Read(string Image, string? Language, string Name, int Pass, GroupingPrototype.Block[] Raw);
    internal record Proposal(string Prefix, Rect Bounds, Rect LayoutBounds);

    internal static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("--leading-edge-fusion output.json saved-sweeps.json...");
        SelfTest();
        var results = new List<object>();
        foreach (var file in args.Skip(1))
        foreach (var image in JsonSerializer.Deserialize<Read[]>(File.ReadAllText(file))!.GroupBy(r => r.Image))
        foreach (var pass in image.GroupBy(r => r.Pass))
        {
            var baseline = pass.Single(r => r.Name == "baseline");
            var a = pass.Single(r => r.Name == "pad-24");
            var b = pass.Single(r => r.Name == "pad-16");
            var fused = baseline.Raw.Select(x => x.Restore()).ToList();
            var changes = baseline.Raw.Select((old, index) =>
            {
                var first = Propose(old, baseline.Raw, a.Raw);
                var second = Propose(old, baseline.Raw, b.Raw);
                bool accepted = (baseline.Language == null || baseline.Language == "JA") &&
                    first != null && second != null && first.Prefix == second.Prefix;
                if (accepted)
                {
                    // Keep baseline recognition metrics. Only expand the source extent to
                    // include the confirmed prefix; alternative groups are never imported.
                    var bounds = fused[index].Bounds;
                    bounds.Union(first!.Bounds);
                    var layout = fused[index].LayoutBounds;
                    layout.Union(first.LayoutBounds);
                    fused[index] = fused[index] with { Text = first.Prefix + old.Text,
                        Bounds = bounds, LayoutBounds = layout };
                }
                return new { Index = index, Before = old.Text,
                    After = accepted ? first!.Prefix + old.Text : old.Text,
                    Accepted = accepted, Pad24 = first?.Prefix, Pad16 = second?.Prefix };
            }).ToArray();
            using var bitmap = new System.Drawing.Bitmap(baseline.Image);
            List<OcrTextBlock> Finish(List<OcrTextBlock> raw) =>
                RealtimeTranslationSession.RejectShortReadings(RealtimeTranslationSession.RejectCollapsedBlocks(
                    OcrService.GroupRealtime(raw, bitmap.Height, RealtimeBlockMode.Subtitle), bitmap.Height, -1), -1);
            var before = Finish(baseline.Raw.Select(x => x.Restore()).ToList()).Select(GroupingPrototype.Block.From).ToArray();
            var after = Finish(fused).Select(GroupingPrototype.Block.From).ToArray();
            if (!changes.Any(c => c.Accepted) && JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after))
                throw new Exception("Unmodified inputs changed final output");
            var finalText = Compact(string.Concat(after.Select(x => x.Text)));
            if (before.Any(x => !finalText.Contains(Compact(x.Text), StringComparison.Ordinal)))
                throw new Exception("Fusion lost a previously published reading: " + baseline.Image);
            results.Add(new { baseline.Image, baseline.Language, baseline.Pass, Changes = changes,
                Before = before, After = after });
            Console.WriteLine($"FUSION {Path.GetFileName(baseline.Image)} pass={baseline.Pass}: " +
                string.Join(" | ", changes.Where(c => c.Accepted).Select(c => c.Before + " -> " + c.After)));
        }
        File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\r\n"));
        return 0;
    }

    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
    private static Rect Box(GroupingPrototype.Block b) => new(b.LayoutBounds[0], b.LayoutBounds[1], b.LayoutBounds[2], b.LayoutBounds[3]);
    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= .6 * Math.Min(a.Height, b.Height) &&
        Math.Abs(a.Top + a.Height / 2 - b.Top - b.Height / 2) <= .35 * Math.Max(a.Height, b.Height);

    private static Proposal? Propose(GroupingPrototype.Block old, GroupingPrototype.Block[] owners, GroupingPrototype.Block[] alternative)
    {
        string anchor = Compact(old.Text).TrimEnd('。', '、', '.', ',', '!', '?', '！', '？', '…', '」', '』');
        if (anchor.Count(char.IsLetterOrDigit) < 4) return null;
        var rect = Box(old);
        var row = alternative.Where(x => SameRow(rect, Box(x)) &&
            Box(x).Right >= rect.Left - rect.Height && Box(x).Left <= rect.Right &&
            (x.Confidence == null || x.Confidence >= .8)).OrderBy(x => Box(x).Left).ToArray();
        if (row.Length == 0) return null;
        // Do not absorb another baseline-owned inline fragment or a character name.
        if (owners.Any(x => !ReferenceEquals(x, old) && row.Any(r => Rect.Intersect(Box(x), Box(r)).Width > 0 && SameRow(Box(x), Box(r))))) return null;
        for (int i = 1; i < row.Length; i++)
            if (Box(row[i]).Left - Box(row[i - 1]).Right > rect.Height) return null;
        var union = Box(row[0]);
        foreach (var part in row.Skip(1)) union.Union(Box(part));
        if (union.Left >= rect.Left || Math.Abs(union.Right - rect.Right) > rect.Height * .5) return null;
        string candidate = string.Concat(row.Select(x => Compact(x.Text)));
        int start = candidate.IndexOf(anchor, StringComparison.Ordinal);
        if (start <= 0 || candidate.LastIndexOf(anchor, StringComparison.Ordinal) != start) return null;
        string suffix = candidate[(start + anchor.Length)..];
        if (suffix.Any(char.IsLetterOrDigit)) return null;
        string prefix = candidate[..start];
        if (!prefix.Any(char.IsLetterOrDigit)) return null;
        var renderUnion = row[0].Restore().Bounds;
        foreach (var part in row.Skip(1)) renderUnion.Union(part.Restore().Bounds);
        return new(prefix, renderUnion, union);
    }

    private static void SelfTest()
    {
        GroupingPrototype.Block Block(string text, double x, double y, double w) =>
            new(text, [x, y, w, 30], [x, y, w, 30], default, null, null, .99);
        var old = Block("じゃあ次はね……", 100, 0, 200);
        void Check(bool value, string name) { if (!value) throw new Exception("Fusion guard failed: " + name); }
        Check(Propose(old, [old], [Block("うん、じゃあ次はね", 50, 0, 250)])?.Prefix == "うん、", "preserve baseline punctuation");
        Check(Propose(old, [old], [Block("うん、じゃあ次だね", 50, 0, 250)]) == null, "substitution");
        Check(Propose(old, [old], [Block("うん、じゃあ次はね別の文", 50, 0, 250)]) == null, "new suffix");
        Check(Propose(old, [old], [Block("うん、じゃあ次はね", 50, -60, 250)]) == null, "different row");
        var name = Block("しろは", 20, 0, 70);
        Check(Propose(old, [old, name], [Block("しろはじゃあ次はね", 20, 0, 280)]) == null, "owned prefix");
        // Deliberate counterexample: agreement alone cannot prove an added word exists.
        Check(Propose(old, [old], [Block("偽物じゃあ次はね", 50, 0, 250)]) != null, "documented shared hallucination limitation");
        Console.WriteLine("Fusion self-checks: 6 PASS (includes known shared-hallucination limitation)");
    }
}
