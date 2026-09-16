using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>Text removal, soft blur and a translucent colour wash, composited into an opaque screenshot plate.</summary>
internal sealed record CaptureBackgroundSurface(BitmapSource Image, Color TextColor)
{
    public ImageBrush CreateBrush()
    {
        var brush = new ImageBrush(Image) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    public static CaptureBackgroundSurface? Create(Bitmap frame, TranslatedBlock block,
        IReadOnlyList<Rect> textBounds, Color preferredText)
    {
        var bounds = block.Bounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height)
            || bounds.Width < 4 || bounds.Height < 4) return null;
        const int guard = 24;
        var area = Rect.Intersect(new Rect(bounds.X - guard, bounds.Y - guard,
            bounds.Width + guard * 2, bounds.Height + guard * 2), new Rect(0, 0, frame.Width, frame.Height));
        if (area.IsEmpty) return null;
        var pixelsArea = Rectangle.FromLTRB((int)area.Left, (int)area.Top, (int)Math.Ceiling(area.Right), (int)Math.Ceiling(area.Bottom));
        if (PixelWindow.Read(frame, pixelsArea) is not { } pixels) return null;
        var wash = block.BackgroundColor.A != 0 ? block.BackgroundColor
            : SourceTextColorSampler.ForCaptureOverlay(frame, bounds).Background;
        // A flat UI surface has no missing texture to reconstruct. Inpainting its whole label
        // would borrow the page outside the button and introduce a false bevel or highlight.
        var inner = Rect.Intersect(bounds, area);
        var votes = new Dictionary<int, (long R, long G, long B, int Count)>();
        int samples = 0;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(inner.Width * inner.Height / 4096)));
        for (int y = (int)Math.Ceiling(inner.Top); y < (int)inner.Bottom; y += step)
        for (int x = (int)Math.Ceiling(inner.Left); x < (int)inner.Right; x += step)
        {
            var c = pixels.At(x, y);
            int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
            var vote = votes.GetValueOrDefault(key);
            votes[key] = (vote.R + c.R, vote.G + c.G, vote.B + c.B, vote.Count + 1);
            samples++;
        }
        if (samples >= 16)
        {
            var winner = votes.Values.OrderByDescending(v => v.Count).First();
            var color = Color.FromRgb((byte)(winner.R / winner.Count), (byte)(winner.G / winner.Count), (byte)(winner.B / winner.Count));
            if (winner.Count >= samples * .55 && Math.Abs(color.R - wash.R) + Math.Abs(color.G - wash.G) + Math.Abs(color.B - wash.B) <= 45)
                return Compose([color], 1, 1, preferredText);
        }
        using var original = new Mat(pixelsArea.Height, pixelsArea.Width, MatType.CV_8UC3);
        var rowBytes = new byte[pixelsArea.Width * 3];
        for (int y = 0; y < pixelsArea.Height; y++)
        {
            for (int x = 0; x < pixelsArea.Width; x++)
            {
                var c = pixels.At(x + pixelsArea.X, y + pixelsArea.Y);
                rowBytes[x * 3] = c.B; rowBytes[x * 3 + 1] = c.G; rowBytes[x * 3 + 2] = c.R;
            }
            Marshal.Copy(rowBytes, 0, original.Ptr(y), rowBytes.Length);
        }
        using var mask = new Mat(original.Size(), MatType.CV_8UC1, Scalar.Black);
        foreach (var text in textBounds)
        {
            if (text.IsEmpty || !double.IsFinite(text.X + text.Y + text.Width + text.Height)) continue;
            var padded = text;
            double fringe = Math.Clamp(Math.Min(text.Width, text.Height) * .15, 3, 6);
            padded.Inflate(fringe, fringe);
            var clipped = Rect.Intersect(padded, area);
            if (clipped.IsEmpty) continue;
            int left = Math.Clamp((int)Math.Floor(clipped.Left) - pixelsArea.X, 0, mask.Width);
            int top = Math.Clamp((int)Math.Floor(clipped.Top) - pixelsArea.Y, 0, mask.Height);
            int right = Math.Clamp((int)Math.Ceiling(clipped.Right) - pixelsArea.X, left, mask.Width);
            int bottom = Math.Clamp((int)Math.Ceiling(clipped.Bottom) - pixelsArea.Y, top, mask.Height);
            if (right > left && bottom > top)
                Cv2.Rectangle(mask, new OpenCvSharp.Rect(left, top, right - left, bottom - top), Scalar.White, -1);
        }
        int missing = Cv2.CountNonZero(mask);
        if (missing >= mask.Rows * mask.Cols) return null;
        // Screenshot text is removed as complete source-line bands. The wash hides the remaining
        // low-frequency repair artifacts; glyph-perfect texture restoration is not required here.
        double scale = Math.Min(1, 640.0 / Math.Max(original.Width, original.Height));
        var workSize = new CvSize(Math.Max(1, (int)Math.Round(original.Width * scale)), Math.Max(1, (int)Math.Round(original.Height * scale)));
        using var small = new Mat(); using var smallMask = new Mat(); using var repaired = new Mat();
        Cv2.Resize(original, small, workSize, interpolation: InterpolationFlags.Area);
        Cv2.Resize(mask, smallMask, workSize, interpolation: InterpolationFlags.Area);
        Cv2.Threshold(smallMask, smallMask, 0, 255, ThresholdTypes.Binary);
        if (Cv2.CountNonZero(smallMask) >= smallMask.Rows * smallMask.Cols) return null;
        if (missing > 0) Cv2.Inpaint(small, smallMask, repaired, 3, InpaintTypes.NS);
        else small.CopyTo(repaired);
        using var blurred = new Mat();
        double sigma = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * .04, 1.5, 4) * scale;
        Cv2.GaussianBlur(repaired, blurred, new CvSize(0, 0), Math.Max(.5, sigma));
        using var full = new Mat();
        Cv2.Resize(blurred, full, original.Size(), interpolation: InterpolationFlags.Linear);
        var visible = Rect.Intersect(bounds, new Rect(0, 0, frame.Width, frame.Height));
        if (visible.IsEmpty) return null;
        var crop = new OpenCvSharp.Rect((int)visible.Left - pixelsArea.X, (int)visible.Top - pixelsArea.Y,
            Math.Max(1, (int)visible.Width), Math.Max(1, (int)visible.Height));
        using var interior = new Mat(full, crop);
        double outputScale = Math.Min(1, 256.0 / Math.Max(crop.Width, crop.Height));
        int columns = Math.Max(1, (int)Math.Round(crop.Width * outputScale));
        int rows = Math.Max(1, (int)Math.Round(crop.Height * outputScale));
        using var resized = new Mat();
        Cv2.Resize(interior, resized, new CvSize(columns, rows), interpolation: InterpolationFlags.Area);
        double washOpacity = missing > mask.Rows * mask.Cols * .65 ? .55 : .4;
        var colors = new Color[columns * rows];
        var buffer = new byte[columns * 3];
        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(resized.Ptr(y), buffer, 0, buffer.Length);
            for (int x = 0; x < columns; x++)
                colors[y * columns + x] = Mix(Color.FromRgb(buffer[x * 3 + 2], buffer[x * 3 + 1], buffer[x * 3]), wash, washOpacity);
        }
        return Compose(colors, columns, rows, preferredText);
    }

    private static CaptureBackgroundSurface Compose(Color[] colors, int columns, int rows, Color preferredText)
    {
        double Worst(Color text) => colors.Min(c => OverlayTextColor.ContrastRatio(text, c));
        var foreground = preferredText;
        if (Worst(foreground) < 3.5)
        {
            // One uniform tint preserves the spatial gradient; never paste flat patches behind words.
            var target = OverlayTextColor.ContrastRatio(foreground, Colors.Black) >=
                OverlayTextColor.ContrastRatio(foreground, Colors.White) ? Colors.Black : Colors.White;
            double low = 0, high = 1;
            for (int i = 0; i < 12; i++)
            {
                double mid = (low + high) / 2;
                if (colors.All(c => OverlayTextColor.ContrastRatio(foreground, Mix(c, target, mid)) >= 3.5)) high = mid;
                else low = mid;
            }
            for (int i = 0; i < colors.Length; i++) colors[i] = Mix(colors[i], target, high);
        }
        var bytes = new byte[colors.Length * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            bytes[i * 4] = colors[i].B; bytes[i * 4 + 1] = colors[i].G; bytes[i * 4 + 2] = colors[i].R; bytes[i * 4 + 3] = 255;
        }
        var image = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, bytes, columns * 4);
        image.Freeze();
        return new(image, foreground);
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));
}
