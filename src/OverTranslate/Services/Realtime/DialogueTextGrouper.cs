using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Dialogue-box geometry, independent of screenshot paragraph, punctuation and heading rules.
/// Horizontal rows are completed first. An unresolved row is never partially joined vertically.
/// </summary>
internal static class DialogueTextGrouper
{
    internal static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> input, GroupingTrace? trace = null)
    {
        if (trace is not null && trace.Blocks.Count == 0) trace.RegisterBlocks(input);
        if (input.Count <= 1)
        {
            foreach (var b in input) trace?.RegisterLine(b, [b]);
            trace?.OrderLines(input);
            trace?.RegisterGroups(input.Select(b => (IReadOnlyList<OcrTextBlock>)new[] { b }).ToList());
            return input.ToList();
        }
        var rows = new List<List<OcrTextBlock>>();
        foreach (var block in input.OrderBy(b => b.LayoutBounds.X).ThenBy(b => b.LayoutBounds.Y))
        {
            var row = rows.FirstOrDefault(r => r.Any(b => SameRow(b.LayoutBounds, block.LayoutBounds)));
            if (row is null) rows.Add([block]);
            else row.Add(block);
        }
        var lines = new List<(OcrTextBlock Block, bool Unresolved)>();
        foreach (var row in rows)
        {
            var segments = new List<List<OcrTextBlock>>();
            foreach (var block in row)
            {
                if (segments.Count == 0 || !Adjacent(segments[^1][^1].LayoutBounds, block.LayoutBounds))
                    segments.Add([block]);
                else segments[^1].Add(block);
            }
            // Single-character detections need a real text fragment as an anchor. Joining only
            // isolated symbols would manufacture enough length to evade the downstream filter.
            segments = segments.SelectMany(s => s.Count > 1 && s.All(b => b.Text.Count(c => !char.IsWhiteSpace(c)) < 2)
                ? s.Select(b => new List<OcrTextBlock> { b }) : new[] { s }).ToList();
            foreach (var segment in segments)
            {
                var line = OcrTextBlockGrouper.BuildGroup(segment);
                if (segment.Count > 1)
                    line = line with { Text = segment.Select(b => b.Text.Trim()).Aggregate(OcrTextBlockGrouper.JoinInlineText) };
                trace?.RegisterLine(line, segment);
                lines.Add((line, segments.Count > 1));
            }
        }
        var groups = new List<List<OcrTextBlock>>();
        var open = new List<bool>();
        trace?.OrderLines(lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X).Select(l => l.Block).ToList());
        foreach (var line in lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X))
        {
            int target = -1;
            double nearest = double.MaxValue;
            for (int i = 0; !line.Unresolved && i < groups.Count; i++)
            {
                if (!open[i]) continue;
                var previous = groups[i][^1].LayoutBounds;
                var current = line.Block.LayoutBounds;
                if (!NextRow(previous, current)) continue;
                var distance = current.Y - previous.Y;
                if (distance < nearest) { target = i; nearest = distance; }
            }
            // A line we did not join is a boundary for older overlapping text. Do not later
            // jump across a speaker label or a differently sized line to resume that group.
            for (int i = 0; i < groups.Count; i++)
            {
                if (i == target) continue;
                var previous = groups[i][^1].LayoutBounds;
                var current = line.Block.LayoutBounds;
                if (current.Y > previous.Y && Math.Min(previous.Right, current.Right) > Math.Max(previous.Left, current.Left))
                    open[i] = false;
            }
            if (target < 0) { groups.Add([line.Block]); open.Add(!line.Unresolved); }
            else groups[target].Add(line.Block);
        }
        trace?.RegisterGroups(groups);
        return groups.Select(OcrTextBlockGrouper.BuildGroup).ToList();
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * 0.6 &&
        Math.Abs((a.Top + a.Bottom - b.Top - b.Bottom) / 2) <= Math.Max(a.Height, b.Height) * 0.35;

    private static bool Adjacent(Rect a, Rect b) =>
        SameRow(a, b) && Math.Min(a.Height, b.Height) >= Math.Max(a.Height, b.Height) * 0.6 &&
        b.Left - a.Right <= Math.Max(a.Height, b.Height) * 1.5;

    private static bool NextRow(Rect a, Rect b)
    {
        double height = Math.Max(a.Height, b.Height);
        // Long subtitle rows can have quite different detector padding even at the same font
        // size. Give two substantial, similarly wide rows extra height tolerance; a short name
        // or isolated symbol cannot borrow it from the dialogue beside it.
        bool substantialRows = a.Width >= a.Height * 6 && b.Width >= b.Height * 6 &&
            Math.Min(a.Width, b.Width) >= Math.Max(a.Width, b.Width) * 0.5;
        if (height <= 0 || b.Y <= a.Y || SameRow(a, b) ||
            Math.Min(a.Height, b.Height) < height * (substantialRows ? 0.6 : 0.75) ||
            b.Top - a.Bottom > height * 0.65 || b.Top - a.Bottom < -height * 0.25)
            return false;
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (overlap < Math.Min(a.Width, b.Width) * 0.5) return false;
        var alignment = Math.Min(Math.Abs(a.Left - b.Left), Math.Min(Math.Abs(a.Right - b.Right),
            Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2)));
        return alignment <= height * 1.25;
    }
}
