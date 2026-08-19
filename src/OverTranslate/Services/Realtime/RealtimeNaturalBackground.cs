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
/// <remarks>
/// Both halves of this file read a bitmap through one lock and a managed buffer rather than through
/// GDI+'s per-pixel accessors, because "lightweight" was not true of the first version: erasing a
/// line locked the whole patch and copied it three times over, and sampling a colour called GetPixel
/// — which locks and unlocks the bitmap on every call — some fifteen thousand times for one line.
/// Both ran on the thread drawing the interface. Neither the output nor the algorithm changed with
/// the buffers; only what it costs to arrive at them.
/// </remarks>
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

        if (sourceLineBounds.Count == 0)
            return patch;

        // One lock and one copy each way for the whole patch, however many lines are erased into it.
        // Every line reads only pixels outside the area it writes — see EraseSourceText — so they can
        // all work in this one buffer, and each still sees the lines erased before it exactly as it
        // did when every line locked the bitmap for itself.
        var data = patch.LockBits(
            new Rectangle(0, 0, patch.Width, patch.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * patch.Height];
            for (int y = 0; y < patch.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);

            foreach (var source in sourceLineBounds)
            {
                var local = Rectangle.FromLTRB(
                    (int)Math.Floor(source.Left) - clippedPatch.Left,
                    (int)Math.Floor(source.Top) - clippedPatch.Top,
                    (int)Math.Ceiling(source.Right) - clippedPatch.Left,
                    (int)Math.Ceiling(source.Bottom) - clippedPatch.Top);

                EraseSourceText(pixels, stride, patch.Width, patch.Height, local);
            }

            for (int y = 0; y < patch.Height; y++)
                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
        }
        finally
        {
            patch.UnlockBits(data);
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

        var inner = Clamp(bounds, frame.Width, frame.Height);
        if (inner.Width <= 0 || inner.Height <= 0)
            return fallback;

        // The ring the background is read from and the box the glyphs are read from, in one window:
        // the two passes below and the dominant-background pass all sample inside it, so the bitmap
        // is locked once for the whole decision.
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        var outer = Rectangle.FromLTRB(
            Math.Clamp(inner.Left - padX, 0, frame.Width),
            Math.Clamp(inner.Top - padY, 0, frame.Height),
            Math.Clamp(inner.Right + padX, 0, frame.Width),
            Math.Clamp(inner.Bottom + padY, 0, frame.Height));
        if (outer.Width <= 0 || outer.Height <= 0)
            return fallback;

        if (PixelWindow.Read(frame, outer) is not { } window)
            return fallback;

        var bg = SampleDominantBackground(window, outer, inner);

        int maxDiff = 0;
        for (int y = inner.Top; y < inner.Bottom; y += 2)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                var c = window.At(x, y);
                int diff = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (diff > maxDiff) maxDiff = diff;
            }
        }

        if (maxDiff < 36)
            return fallback;

        int threshold = Math.Max(54, (int)Math.Round(maxDiff * 0.58));
        long r = 0, g = 0, b = 0, count = 0;
        for (int y = inner.Top; y < inner.Bottom; y += 2)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                var c = window.At(x, y);
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
        var background = MediaColor.FromRgb(bg.R, bg.G, bg.B);

        // Nothing is corrected until it is known to be worth keeping: the fallback is the colour the
        // user chose, and tuning that would be overruling them rather than repairing a measurement.
        if (!SeparatesFrom(sampled, background))
            return fallback;

        return OverlayTextColor.Tune(sampled, background);
    }

    /// <summary>
    /// Fills one source line's rectangle with colour interpolated from its surroundings.
    /// </summary>
    /// <remarks>
    /// Reads only outside the rectangle it writes: the row above and below, or the column either
    /// side, or the ring around it. That is what lets several lines share one buffer — there is no
    /// order in which one line's fill can be read as though it were the picture underneath.
    /// </remarks>
    private static void EraseSourceText(byte[] pixels, int stride, int width, int height, Rectangle source)
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
        var rect = Rectangle.Intersect(new Rectangle(0, 0, width, height), expanded);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        bool hasTopBottom = rect.Top > 0 && rect.Bottom < height;
        bool hasLeftRight = rect.Left > 0 && rect.Right < width;

        if (hasTopBottom)
        {
            int topRow = (rect.Top - 1) * stride;
            int bottomRow = rect.Bottom * stride;

            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                double t = (y - rect.Top + 1.0) / (rect.Height + 1.0);
                int dstRow = y * stride;

                for (int x = rect.Left; x < rect.Right; x++)
                {
                    int dst = dstRow + x * 4;
                    int a = topRow + x * 4;
                    int b = bottomRow + x * 4;
                    pixels[dst] = Lerp(pixels[a], pixels[b], t);
                    pixels[dst + 1] = Lerp(pixels[a + 1], pixels[b + 1], t);
                    pixels[dst + 2] = Lerp(pixels[a + 2], pixels[b + 2], t);
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
                int row = y * stride;
                int left = row + leftX * 4;
                int right = row + rightX * 4;

                for (int x = rect.Left; x < rect.Right; x++)
                {
                    double t = (x - rect.Left + 1.0) / (rect.Width + 1.0);
                    int dst = row + x * 4;
                    pixels[dst] = Lerp(pixels[left], pixels[right], t);
                    pixels[dst + 1] = Lerp(pixels[left + 1], pixels[right + 1], t);
                    pixels[dst + 2] = Lerp(pixels[left + 2], pixels[right + 2], t);
                    pixels[dst + 3] = 255;
                }
            }
        }
        else
        {
            // Taken before anything is written, which is also why it can read the same buffer: every
            // pixel it averages lies outside the rectangle about to be filled.
            var fill = BorderAverage(pixels, stride, width, height, rect);

            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                int row = y * stride;
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
    }

    private static GdiColor BorderAverage(
        byte[] pixels,
        int stride,
        int width,
        int height,
        Rectangle rect)
    {
        long r = 0, g = 0, b = 0, n = 0;

        void Add(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int i = y * stride + x * 4;
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

    private static GdiColor SampleDominantBackground(
        PixelWindow window, Rectangle outer, Rectangle inner)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int y = outer.Top; y < outer.Bottom; y += 2)
        {
            for (int x = outer.Left; x < outer.Right; x += 2)
            {
                bool insideText = x >= inner.Left && x < inner.Right && y >= inner.Top && y < inner.Bottom;
                if (insideText) continue;

                var c = window.At(x, y);
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

    /// <summary>
    /// Whether what was sampled stands far enough from its background to be worth drawing.
    /// </summary>
    /// <remarks>
    /// The source may depend on an outline that sampling cannot reproduce. Where it does, the two
    /// averages come out close together, and the user's configured colour is safer than a foreground
    /// that is nearly invisible.
    /// </remarks>
    private static bool SeparatesFrom(MediaColor sampled, MediaColor background) =>
        Math.Abs(sampled.R - background.R) +
        Math.Abs(sampled.G - background.G) +
        Math.Abs(sampled.B - background.B) >= 90;

    private static Rectangle Clamp(WpfRect rect, int width, int height) => Rectangle.FromLTRB(
        Math.Clamp((int)Math.Floor(rect.Left), 0, width),
        Math.Clamp((int)Math.Floor(rect.Top), 0, height),
        Math.Clamp((int)Math.Ceiling(rect.Right), 0, width),
        Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, height));

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>
    /// One rectangle of a bitmap's pixels, read out once so a sampler can index it in the bitmap's
    /// own coordinates.
    /// </summary>
    /// <remarks>
    /// The alternative is <c>Bitmap.GetPixel</c>, which locks the bitmap, reads four bytes and
    /// unlocks it again on every call. Sampling one subtitle line asks for around fifteen thousand
    /// pixels, so this is the difference between one lock and fifteen thousand.
    /// </remarks>
    private readonly struct PixelWindow(byte[] pixels, int stride, Rectangle area)
    {
        public static PixelWindow? Read(Bitmap frame, Rectangle area)
        {
            BitmapData? data = null;
            try
            {
                data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                int stride = area.Width * 4;
                var pixels = new byte[stride * area.Height];
                for (int y = 0; y < area.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);

                return new PixelWindow(pixels, stride, area);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (data is not null) frame.UnlockBits(data);
            }
        }

        /// <param name="x">In the source bitmap's coordinates, not the window's.</param>
        public GdiColor At(int x, int y)
        {
            int i = (y - area.Top) * stride + (x - area.Left) * 4;
            return GdiColor.FromArgb(255, pixels[i + 2], pixels[i + 1], pixels[i]);
        }
    }
}
