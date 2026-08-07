using System.Text;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Decides whether two recognitions of the same region are showing the same words.
/// </summary>
/// <remarks>
/// Recognition is not deterministic across frames. Reading the same unchanged subtitle repeatedly
/// returned 222, 227, 222 and 230 characters in one measured session — a glyph on a boundary,
/// antialiasing that shifted as something moved behind it, a stray mark picked up and dropped again.
/// Compared with <c>==</c>, every one of those reads as new text: it is translated again and the
/// overlay is rebuilt, which the reader sees as the line flickering while it says the same thing.
///
/// So a small difference over a long line is treated as the same line. The tolerance is proportional
/// because that is how the noise behaves — a few characters in two hundred — and it is withheld
/// entirely from short text, where a couple of characters is not noise but the whole meaning
/// ("HP 100" against "HP 190", "Yes" against "No").
/// </remarks>
internal static class TextSimilarity
{
    /// <summary>
    /// Below this length every character is compared exactly. Short strings carry too much meaning
    /// per character to spend any of it on tolerance.
    /// </summary>
    public const int MinLengthForTolerance = 12;

    /// <summary>
    /// The share of a line that may differ and still count as the same line. Comfortably above the
    /// couple of percent that recognition noise produces, and far below what replacing the words
    /// would produce.
    /// </summary>
    public const double MaxDifferenceRatio = 0.1;

    public static bool IsSameContent(string a, string b)
    {
        if (ReferenceEquals(a, b)) return true;

        // Spacing is where recognition wobbles most, and it is also what carries the least meaning,
        // so it is normalised away before anything is measured.
        var left = NormaliseWhitespace(a);
        var right = NormaliseWhitespace(b);

        if (left == right) return true;
        if (left.Length == 0 || right.Length == 0) return false;

        var longest = Math.Max(left.Length, right.Length);
        if (longest < MinLengthForTolerance) return false;

        var allowed = Math.Max(1, (int)(longest * MaxDifferenceRatio));

        // Two strings whose lengths already differ by more than the budget cannot be within it, and
        // this is much cheaper than finding out the long way.
        if (Math.Abs(left.Length - right.Length) > allowed) return false;

        return EditDistanceWithin(left, right, allowed);
    }

    /// <summary>
    /// Levenshtein distance, answered only as "within <paramref name="allowed"/> or not" so the
    /// computation can stop as soon as the answer is no.
    /// </summary>
    private static bool EditDistanceWithin(string a, string b, int allowed)
    {
        // Two rows rather than the full matrix: nothing here needs the path, only the distance.
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var bestInRow = current[0];

            for (int j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;

                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
                bestInRow = Math.Min(bestInRow, current[j]);
            }

            // Every remaining path runs through this row, so once its best cell is over budget the
            // final distance must be too.
            if (bestInRow > allowed) return false;

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= allowed;
    }

    private static string NormaliseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && builder.Length > 0) builder.Append(' ');
            inWhitespace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
}
