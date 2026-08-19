using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Services;

/// <summary>
/// Corrects a text colour read off the screen into one worth drawing back over it.
/// </summary>
/// <remarks>
/// A sampled colour is an average of the pixels that stood out from the local background, and every
/// one of those averages is pulled toward the background by the antialiased rim around each glyph.
/// The result reads darker and flatter than the text it was taken from — a red comes back closer to
/// maroon, a light grey closer to mid grey — which is not what a reader compares it against: they
/// have the original line in view. So the hue is kept, the saturation is put back up, and the
/// lightness is held far enough from the background to stay legible.
///
/// <para>Shared by the two places that draw over a picture: the capture overlay, where it has always
/// run, and realtime subtitles, which sampled the same way and then drew the average as it came.</para>
/// </remarks>
internal static class OverlayTextColor
{
    /// <param name="text">The colour averaged out of the glyph pixels.</param>
    /// <param name="background">What that text sat on, which decides which way it has to move.</param>
    public static MediaColor Tune(MediaColor text, MediaColor background)
    {
        double bgLum = PerceivedLuminance(background);
        double textLum = PerceivedLuminance(text);
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

    public static double PerceivedLuminance(MediaColor color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

    private static (double H, double S, double L) RgbToHsl(MediaColor color)
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

    private static MediaColor HslToRgb(double h, double s, double l)
    {
        h = h - Math.Floor(h);
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 0)
        {
            byte gray = (byte)Math.Round(l * 255);
            return MediaColor.FromRgb(gray, gray, gray);
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

        return MediaColor.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
    }
}
