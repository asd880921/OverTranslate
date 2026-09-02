using System.Windows;
using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Services.Ocr;

/// <summary>How one line of text looks: the colour it is written in, and the colour behind it.</summary>
internal readonly record struct BlockAppearance(MediaColor Background, MediaColor Foreground);

/// <summary>
/// Where the grouper gets the colours of a line from, so that it can ask what the picture looks
/// like without holding a bitmap.
/// </summary>
/// <remarks>
/// An interface rather than the bitmap itself because the grouper is pure geometry everywhere else
/// and its tests are written as coordinates. A test that needs two lines on different backgrounds
/// says so directly; nothing has to render a picture to be read back.
/// </remarks>
internal interface IBlockAppearanceSource
{
    /// <summary>The colours of the line occupying <paramref name="bounds"/>.</summary>
    BlockAppearance For(Rect bounds);
}

/// <summary>
/// How far apart two colours look, rather than how far apart their numbers are.
/// </summary>
/// <remarks>
/// <para>Comparing channels directly does not survive a screenshot. Anti-aliasing, ClearType's
/// subpixel rendering, JPEG, and any scaling on the way all leave the same painted colour arriving
/// as a spread of near values, so equality is never true and a plain channel distance says a dark
/// blue and a black are as different as a white and a light grey — which is not what the eye
/// reports, and the eye is what decides whether two lines look like one block.</para>
///
/// <para>So: sRGB to CIELAB, and the plain CIE76 distance between them. Later revisions of the
/// formula exist and are better at telling two close colours apart; nothing here needs that. What
/// this has to separate is a heading from body text and a grey card from a white one, which are far
/// apart in any of them, and CIE76 is the one that can be read and checked by hand.</para>
/// </remarks>
internal static class PerceptualColor
{
    /// <summary>How far apart two colours are, in CIELAB units where about 2.3 is "just noticeable".</summary>
    public static double Distance(MediaColor first, MediaColor second)
    {
        var (l1, a1, b1) = ToLab(first);
        var (l2, a2, b2) = ToLab(second);

        return Math.Sqrt(
            (l1 - l2) * (l1 - l2) +
            (a1 - a2) * (a1 - a2) +
            (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) ToLab(MediaColor color)
    {
        // sRGB to linear, then to CIE XYZ under D65, then to Lab. The constants are the standard
        // ones; nothing here is tuned.
        var r = ToLinear(color.R / 255.0);
        var g = ToLinear(color.G / 255.0);
        var b = ToLinear(color.B / 255.0);

        var x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        var z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

        var fx = Pivot(x);
        var fy = Pivot(y);
        var fz = Pivot(z);

        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double ToLinear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static double Pivot(double value) =>
        value > 0.008856 ? Math.Cbrt(value) : (7.787 * value) + (16.0 / 116.0);
}
