using System.Drawing;
using MediaColor = System.Windows.Media.Color;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>Reads a paragraph's own paper/panel between its lines, before looking outside it.</summary>
internal static class CaptureBackgroundColor
{
    public static MediaColor? Sample(Bitmap frame, TranslatedBlock block, bool vertical)
    {
        if (block.SourceLineBounds is not { Count: > 1 } rows) return null;
        var lines = rows.Where(r => !r.IsEmpty && double.IsFinite(r.X + r.Y + r.Width + r.Height)
                && r.Width > 0 && r.Height > 0)
            .OrderBy(r => vertical ? r.X : r.Y).ToArray();
        var gaps = new List<Rect>();
        for (int i = 1; i < lines.Length; i++)
        {
            var a = lines[i - 1]; var b = lines[i];
            double glyph = vertical ? Math.Min(a.Width, b.Width) : Math.Min(a.Height, b.Height);
            double pad = Math.Clamp(glyph * .08, 2, 4);
            double left = vertical ? a.Right + pad : Math.Max(a.Left, b.Left) + pad;
            double right = vertical ? b.Left - pad : Math.Min(a.Right, b.Right) - pad;
            double top = vertical ? Math.Max(a.Top, b.Top) + pad : a.Bottom + pad;
            double bottom = vertical ? Math.Min(a.Bottom, b.Bottom) - pad : b.Top - pad;
            if (right > left && bottom > top)
            {
                var gap = Rect.Intersect(new Rect(left, top, right - left, bottom - top), block.Bounds);
                gap.Intersect(new Rect(0, 0, frame.Width, frame.Height));
                if (!gap.IsEmpty && gap.Width >= 1 && gap.Height >= 1) gaps.Add(gap);
            }
        }
        var counts = new Dictionary<int, (long R, long G, long B, int Count)>();
        int total = 0;
        foreach (var gap in gaps)
        {
            var area = Rectangle.FromLTRB((int)Math.Ceiling(gap.Left), (int)Math.Ceiling(gap.Top),
                (int)Math.Floor(gap.Right), (int)Math.Floor(gap.Bottom));
            if (area.Width <= 0 || area.Height <= 0 || PixelWindow.Read(frame, area) is not { } pixels) continue;
            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)area.Width * area.Height / 2048)));
            for (int y = area.Top; y < area.Bottom; y += step)
            for (int x = area.Left; x < area.Right; x += step)
            {
                // A third OCR line may overlap the gap between this pair.
                if (lines.Any(r => r.Contains(x, y))) continue;
                var c = pixels.At(x, y);
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                var v = counts.GetValueOrDefault(key);
                counts[key] = (v.R + c.R, v.G + c.G, v.B + c.B, v.Count + 1);
                total++;
            }
        }
        if (total < 16) return null;
        var best = counts.Values.OrderByDescending(v => v.Count).First();
        // Texture or different surfaces do not justify imposing one interior colour.
        if (best.Count < total * .6) return null;
        return MediaColor.FromRgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }
}
