using System.Globalization;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The two colours a realtime subtitle is drawn in, and the rules for reading them back out of the
/// settings file.
/// </summary>
/// <remarks>
/// Only the hue is the user's. The scrim's alpha is fixed here, because it is not a preference: the
/// band exists to hide the source line underneath it, and at a lower alpha the original shows
/// through and the reader gets two overlapping sentences — which is the feature failing at the only
/// thing it does. A higher one would be a solid block over content the user is watching.
/// </remarks>
public static class RealtimeSubtitleColors
{
    public const string DefaultText = "#FAFAFA";
    public const string DefaultScrim = "#000000";

    /// <summary>Opaque enough to hide a subtitle, sheer enough to keep the picture behind it.</summary>
    public const byte ScrimAlpha = 0xB8;

    /// <summary>The translated text's colour, always fully opaque.</summary>
    public static Color Text(string? hex) => Parse(hex) ?? Parse(DefaultText)!.Value;

    /// <summary>The band behind the text, at the fixed alpha above.</summary>
    public static Color Scrim(string? hex)
    {
        var rgb = Parse(hex) ?? Parse(DefaultScrim)!.Value;
        return Color.FromArgb(ScrimAlpha, rgb.R, rgb.G, rgb.B);
    }

    /// <summary>Back to the form stored in the settings file.</summary>
    public static string Format(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Null for anything unreadable rather than throwing. appsettings.json is a file a user can open
    /// and edit, and a typo in a colour should cost them that colour — not the subtitles, and
    /// certainly not the session.
    /// </summary>
    /// <remarks>
    /// An eight-digit value keeps its RGB and loses its alpha instead of being rejected: someone who
    /// writes one is asking for that colour, and the alpha is not theirs to set.
    /// </remarks>
    private static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        var digits = hex.Trim().TrimStart('#');
        if (digits.Length == 8) digits = digits[2..];
        if (digits.Length != 6) return null;

        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;

        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
