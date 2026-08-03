using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace OverTranslate.Services;

/// <summary>
/// Reads the colours a translation bubble should use straight out of the source image, so the
/// bubble blends into the page instead of sitting on it as a foreign white box. Shared by the live
/// screen overlay and the batch image export — both need the same colours for the same region.
/// </summary>
public static class BlockColorSampler
{
    /// <summary>
    /// Samples the background surrounding <paramref name="bounds"/> and the text colour inside it.
    /// <paramref name="data"/> must be locked as 32bpp ARGB.
    /// </summary>
    public static (Color Background, Color Text) Sample(
        BitmapData data, int bitmapWidth, int bitmapHeight, Rect bounds)
    {
        var background = SampleBackground(data, bitmapWidth, bitmapHeight, bounds);
        var text = SampleText(data, bitmapWidth, bitmapHeight, bounds, background);
        return (background, text);
    }

    /// <summary>
    /// Pads outward from the text box and takes the most common surrounding colour. This works for
    /// every script: it stays correct even when the (tightened) box no longer fully encloses the
    /// glyphs. An earlier English-only variant averaged thin bands directly above and below the
    /// box; once the box height was reduced those bands grazed the light glyphs and produced a
    /// washed-out grey that no longer blended with the dark page behind it.
    /// </summary>
    public static Color SampleBackground(BitmapData data, int bitmapWidth, int bitmapHeight, Rect bounds)
    {
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        int x1 = Math.Clamp((int)bounds.X - padX, 0, bitmapWidth);
        int y1 = Math.Clamp((int)bounds.Y - padY, 0, bitmapHeight);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width) + padX, 0, bitmapWidth);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height) + padY, 0, bitmapHeight);
        int innerX1 = Math.Clamp((int)bounds.X, 0, bitmapWidth);
        int innerY1 = Math.Clamp((int)bounds.Y, 0, bitmapHeight);
        int innerX2 = Math.Clamp((int)(bounds.X + bounds.Width), 0, bitmapWidth);
        int innerY2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bitmapHeight);

        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();

        void AddPixel(int px, int py)
        {
            int v = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
            byte b = (byte)(v & 0xFF);
            byte g = (byte)((v >> 8) & 0xFF);
            byte r = (byte)((v >> 16) & 0xFF);
            int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            var bucket = buckets.GetValueOrDefault(key);
            buckets[key] = (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1);
        }

        for (int py = y1; py < y2; py++)
        {
            for (int px = x1; px < x2; px += 2)
            {
                bool insideTextRect = px >= innerX1 && px < innerX2 && py >= innerY1 && py < innerY2;
                if (!insideTextRect)
                    AddPixel(px, py);
            }
        }

        if (buckets.Count == 0)
            return Colors.White;

        var dominant = buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .First();

        return Color.FromRgb(
            (byte)(dominant.R / dominant.Count),
            (byte)(dominant.G / dominant.Count),
            (byte)(dominant.B / dominant.Count));
    }

    public static Color SampleText(
        BitmapData data, int bitmapWidth, int bitmapHeight, Rect bounds, Color background)
    {
        int x1 = Math.Clamp((int)bounds.X, 0, bitmapWidth);
        int y1 = Math.Clamp((int)bounds.Y, 0, bitmapHeight);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width),  0, bitmapWidth);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bitmapHeight);

        int maxDiff = 0;
        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px += 2)
            {
                int v  = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                byte vB = (byte)(v & 0xFF);
                byte vG = (byte)((v >> 8) & 0xFF);
                byte vR = (byte)((v >> 16) & 0xFF);
                int diff = Math.Abs(vR - background.R) + Math.Abs(vG - background.G) + Math.Abs(vB - background.B);
                if (diff > maxDiff)
                    maxDiff = diff;
            }

        int diffThreshold = Math.Max(60, (int)(maxDiff * 0.6));
        long r = 0, g = 0, b = 0, n = 0;
        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px += 2)
            {
                int v  = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                byte vB = (byte)(v & 0xFF);
                byte vG = (byte)((v >> 8) & 0xFF);
                byte vR = (byte)((v >> 16) & 0xFF);
                int diff = Math.Abs(vR - background.R) + Math.Abs(vG - background.G) + Math.Abs(vB - background.B);
                if (diff >= diffThreshold) { r += vR; g += vG; b += vB; n++; }
            }

        if (n == 0)
        {
            double lum = GetPerceivedLuminance(background);
            return lum > 0.5 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255);
        }

        return Tune(Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n)), background);
    }

    /// <summary>
    /// Nudges a sampled text colour so it stays legible on the bubble. Antialiasing pulls sampled
    /// glyph colours toward the background, so this pushes contrast and saturation back out while
    /// keeping the hue the source actually used.
    /// </summary>
    public static Color Tune(Color text, Color background)
    {
        double bgLum = GetPerceivedLuminance(background);
        double textLum = GetPerceivedLuminance(text);
        var (h, s, l) = RgbToHsl(text);
        bool isNearNeutral = s < 0.18;
        bool isNearBlack = textLum < 0.16;

        if (isNearNeutral)
        {
            if (isNearBlack)
                return text;

            if (bgLum >= 0.55)
            {
                double maxAllowedLum = Math.Max(0.08, bgLum - 0.24);
                double boostedLum = Math.Min(maxAllowedLum, textLum + 0.12);
                double boostedLightness = Math.Min(0.74, l + 0.1);
                return HslToRgb(h, Math.Min(0.22, s * 1.08), Math.Max(boostedLightness, boostedLum));
            }

            double minAllowedLum = Math.Min(0.92, bgLum + 0.3);
            double liftedLum = Math.Max(minAllowedLum, textLum + 0.08);
            double liftedLightness = Math.Max(l, Math.Min(0.9, l + 0.08));
            return HslToRgb(h, Math.Min(0.22, s * 1.08), Math.Max(liftedLightness, liftedLum));
        }

        // For colored text, preserve hue and only nudge brightness slightly.
        // The main correction is stronger saturation so sampled colors feel closer
        // to the source instead of getting washed out by antialiasing.
        double targetSaturation = Math.Min(1.0, Math.Max(s + 0.08, s * 1.12));

        if (bgLum >= 0.55)
        {
            double maxAllowedLum = Math.Max(0.08, bgLum - 0.24);
            double adjustedLum = Math.Min(maxAllowedLum, textLum + 0.01);
            double adjustedLightness = Math.Min(0.64, Math.Max(l, l + 0.01));
            return HslToRgb(h, targetSaturation, Math.Max(adjustedLightness, adjustedLum));
        }

        double minAllowedColorLum = Math.Min(0.9, bgLum + 0.22);
        double colorLum = Math.Max(minAllowedColorLum, textLum + 0.01);
        double colorLightness = Math.Max(l, Math.Min(0.8, l + 0.01));
        return HslToRgb(h, targetSaturation, Math.Max(colorLightness, colorLum));
    }

    public static double GetPerceivedLuminance(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

    private static (double H, double S, double L) RgbToHsl(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0;
        double l = (max + min) / 2.0;

        if (Math.Abs(max - min) < double.Epsilon)
            return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        if (Math.Abs(max - r) < double.Epsilon)
            h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
        else if (Math.Abs(max - g) < double.Epsilon)
            h = ((b - r) / d + 2) / 6.0;
        else
            h = ((r - g) / d + 4) / 6.0;

        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        h = h - Math.Floor(h);
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 0)
        {
            byte gray = (byte)Math.Round(l * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        return Color.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
    }
}
