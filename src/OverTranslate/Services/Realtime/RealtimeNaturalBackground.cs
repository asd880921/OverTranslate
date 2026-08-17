using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GdiColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Builds a live-looking background patch for realtime translation. The source glyph rectangle is
/// replaced with colour interpolated from the pixels immediately around it, while every pixel
/// outside the OCR rectangle stays exactly as it was on screen. This is intentionally lightweight:
/// it runs several times per second next to OCR and avoids adding another native/AI dependency.
/// </summary>
internal static class RealtimeNaturalBackground
{
    // OCR rectangles are often tight around the main glyph body. Japanese dakuten/handakuten,
    // punctuation dots and antialiasing can sit well outside that rectangle, which leaves the white
    // "crumbs" visible after replacement. Padding is therefore adaptive to the detected line height
    // rather than a fixed two pixels. The top side is deliberately the largest because detached
    // Japanese marks overwhelmingly live above the glyph body.
    private const int MinErasePadX = 5;
    private const int MinErasePadTop = 8;
    private const int MinErasePadBottom = 4;

    public static Bitmap? CreatePatch(
        Bitmap frame,
        Rectangle patchBounds,
        IReadOnlyList<WpfRect> sourceLineBounds)
    {
        var frameBounds = new Rectangle(0, 0, frame.Width, frame.Height);
        var clippedPatch = Rectangle.Intersect(frameBounds, patchBounds);
        if (clippedPatch.Width <= 0 || clippedPatch.Height <= 0)
            return null;

        Bitmap patch;
        try
        {
            patch = frame.Clone(clippedPatch, PixelFormat.Format32bppArgb);
        }
        catch
        {
            return null;
        }

        foreach (var source in sourceLineBounds)
        {
            var local = Rectangle.FromLTRB(
                (int)Math.Floor(source.Left) - clippedPatch.Left,
                (int)Math.Floor(source.Top) - clippedPatch.Top,
                (int)Math.Ceiling(source.Right) - clippedPatch.Left,
                (int)Math.Ceiling(source.Bottom) - clippedPatch.Top);

            EraseSourceText(patch, local);
        }

        return patch;
    }

    /// <summary>
    /// Samples the source text colour so the replacement keeps roughly the same visual hierarchy as
    /// the original. Falls back to the configured realtime text colour when no convincing foreground
    /// pixels can be separated from the local background.
    /// </summary>
    public static MediaColor SampleTextColor(Bitmap frame, WpfRect bounds, MediaColor fallback)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return fallback;

        var bg = SampleDominantBackground(frame, bounds);
        var area = Clamp(bounds, frame.Width, frame.Height);
        if (area.Width <= 0 || area.Height <= 0)
            return fallback;

        int maxDiff = 0;
        for (int y = area.Top; y < area.Bottom; y += 2)
        {
            for (int x = area.Left; x < area.Right; x += 2)
            {
                var c = frame.GetPixel(x, y);
                int diff = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (diff > maxDiff) maxDiff = diff;
            }
        }

        if (maxDiff < 36)
            return fallback;

        int threshold = Math.Max(54, (int)Math.Round(maxDiff * 0.58));
        long r = 0, g = 0, b = 0, count = 0;
        for (int y = area.Top; y < area.Bottom; y += 2)
        {
            for (int x = area.Left; x < area.Right; x += 2)
            {
                var c = frame.GetPixel(x, y);
                int diff = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (diff < threshold) continue;
                r += c.R;
                g += c.G;
                b += c.B;
                count++;
            }
        }

        if (count == 0)
            return fallback;

        var sampled = MediaColor.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
        return EnsureReadable(sampled, MediaColor.FromRgb(bg.R, bg.G, bg.B), fallback);
    }

    private static void EraseSourceText(Bitmap bitmap, Rectangle source)
    {
        int heightBasis = Math.Clamp(source.Height, 12, 72);
        int padX = Math.Max(MinErasePadX, (int)Math.Ceiling(heightBasis * 0.16));
        int padTop = Math.Max(MinErasePadTop, (int)Math.Ceiling(heightBasis * 0.34));
        int padBottom = Math.Max(MinErasePadBottom, (int)Math.Ceiling(heightBasis * 0.16));

        var expanded = Rectangle.FromLTRB(
            source.Left - padX,
            source.Top - padTop,
            source.Right + padX,
            source.Bottom + padBottom);
        var rect = Rectangle.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height), expanded);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
            var original = (byte[])pixels.Clone();

            int RowOffset(int y) => y * stride;

            bool hasTopBottom = rect.Top > 0 && rect.Bottom < bitmap.Height;
            bool hasLeftRight = rect.Left > 0 && rect.Right < bitmap.Width;

            if (hasTopBottom)
            {
                int topY = rect.Top - 1;
                int bottomY = rect.Bottom;
                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    double t = (y - rect.Top + 1.0) / (rect.Height + 1.0);
                    int dstRow = RowOffset(y);
                    int topRow = RowOffset(topY);
                    int bottomRow = RowOffset(bottomY);

                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        int dst = dstRow + x * 4;
                        int a = topRow + x * 4;
                        int b = bottomRow + x * 4;
                        pixels[dst] = Lerp(original[a], original[b], t);
                        pixels[dst + 1] = Lerp(original[a + 1], original[b + 1], t);
                        pixels[dst + 2] = Lerp(original[a + 2], original[b + 2], t);
                        pixels[dst + 3] = 255;
                    }
                }
            }
            else if (hasLeftRight)
            {
                int leftX = rect.Left - 1;
                int rightX = rect.Right;
                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    int row = RowOffset(y);
                    int left = row + leftX * 4;
                    int right = row + rightX * 4;
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        double t = (x - rect.Left + 1.0) / (rect.Width + 1.0);
                        int dst = row + x * 4;
                        pixels[dst] = Lerp(original[left], original[right], t);
                        pixels[dst + 1] = Lerp(original[left + 1], original[right + 1], t);
                        pixels[dst + 2] = Lerp(original[left + 2], original[right + 2], t);
                        pixels[dst + 3] = 255;
                    }
                }
            }
            else
            {
                var fill = BorderAverage(original, stride, bitmap.Width, bitmap.Height, rect, RowOffset);
                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    int row = RowOffset(y);
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        int dst = row + x * 4;
                        pixels[dst] = fill.B;
                        pixels[dst + 1] = fill.G;
                        pixels[dst + 2] = fill.R;
                        pixels[dst + 3] = 255;
                    }
                }
            }

            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static GdiColor BorderAverage(
        byte[] pixels,
        int stride,
        int width,
        int height,
        Rectangle rect,
        Func<int, int> rowOffset)
    {
        long r = 0, g = 0, b = 0, n = 0;

        void Add(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int i = rowOffset(y) + x * 4;
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
            n++;
        }

        for (int x = rect.Left; x < rect.Right; x++)
        {
            Add(x, rect.Top - 1);
            Add(x, rect.Bottom);
        }
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            Add(rect.Left - 1, y);
            Add(rect.Right, y);
        }

        return n == 0
            ? GdiColor.Black
            : GdiColor.FromArgb(255, (int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static GdiColor SampleDominantBackground(Bitmap frame, WpfRect bounds)
    {
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        int x1 = Math.Clamp((int)Math.Floor(bounds.Left) - padX, 0, frame.Width);
        int y1 = Math.Clamp((int)Math.Floor(bounds.Top) - padY, 0, frame.Height);
        int x2 = Math.Clamp((int)Math.Ceiling(bounds.Right) + padX, 0, frame.Width);
        int y2 = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + padY, 0, frame.Height);
        int innerX1 = Math.Clamp((int)Math.Floor(bounds.Left), 0, frame.Width);
        int innerY1 = Math.Clamp((int)Math.Floor(bounds.Top), 0, frame.Height);
        int innerX2 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, frame.Width);
        int innerY2 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, frame.Height);

        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int y = y1; y < y2; y += 2)
        {
            for (int x = x1; x < x2; x += 2)
            {
                bool inside = x >= innerX1 && x < innerX2 && y >= innerY1 && y < innerY2;
                if (inside) continue;

                var c = frame.GetPixel(x, y);
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                var bucket = buckets.GetValueOrDefault(key);
                buckets[key] = (bucket.R + c.R, bucket.G + c.G, bucket.B + c.B, bucket.Count + 1);
            }
        }

        if (buckets.Count == 0)
            return GdiColor.White;

        var dominant = buckets.Values.OrderByDescending(x => x.Count).First();
        return GdiColor.FromArgb(
            255,
            (int)(dominant.R / dominant.Count),
            (int)(dominant.G / dominant.Count),
            (int)(dominant.B / dominant.Count));
    }

    private static MediaColor EnsureReadable(MediaColor sampled, MediaColor background, MediaColor fallback)
    {
        double distance = Math.Abs(sampled.R - background.R) +
                          Math.Abs(sampled.G - background.G) +
                          Math.Abs(sampled.B - background.B);
        if (distance >= 90)
            return sampled;

        // The source may depend on an outline that sampling cannot reproduce. In that case the user's
        // configured colour is safer than returning a nearly invisible foreground.
        return fallback;
    }

    private static Rectangle Clamp(WpfRect rect, int width, int height) => Rectangle.FromLTRB(
        Math.Clamp((int)Math.Floor(rect.Left), 0, width),
        Math.Clamp((int)Math.Floor(rect.Top), 0, height),
        Math.Clamp((int)Math.Ceiling(rect.Right), 0, width),
        Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, height));

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
