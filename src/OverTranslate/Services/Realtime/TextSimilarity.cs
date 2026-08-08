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

    /// <summary>
    /// How far two readings may diverge and still be the same sentence read twice rather than two
    /// different sentences.
    /// </summary>
    /// <remarks>
    /// Measured over 69 consecutive reads of a live subtitle track. The distribution is strongly
    /// two-humped: 22 pairs under 10% (the same reading, which <see cref="IsSameContent"/> already
    /// absorbs), a handful between 10% and 30% — every one of them the same sentence read
    /// differently ("Where haveyongone!" against "Where have you gone?!") — and 42 pairs above 50%,
    /// every one a real change of line. <b>No pair of genuinely different sentences came in under
    /// 50%, at any length</b>, so anything up to 30% would have been safe on this sample.
    ///
    /// Set at 20% on that first sample and raised to 30% on a much larger one: 693 consecutive
    /// translations from live sessions, with every pair between 20% and 50% read by hand. All 73 of
    /// them were one line read twice — "heard a bunch of experts are there to study it." against
    /// "Mheard a bunchc are there to study it.", the same player UI re-read a dozen ways — and not
    /// one was a real change of line. Raising the bound to 30% catches 52 of them against 32 at
    /// 20%, with nothing wrongly caught at either.
    ///
    /// It stops at 30% because past 40% the pairs stop being one line. There the region holds a
    /// player's furniture as well as its subtitles, and a reading that found a new subtitle differs
    /// from one that missed it by about that much — treating those as the same line would hold the
    /// old translation on screen and lose the new subtitle, which is the failure this whole feature
    /// exists to prevent, and it is far more expensive than a needless repaint.
    ///
    /// Not scaled further by length, though the temptation is real. A proportional bound already
    /// tightens with length on its own — 3 characters at the 12-character floor against 12 at 40 —
    /// and every additional narrowing tried against the sample removed a true re-reading before it
    /// removed any false one, starting with a 21-character line that differed by 19%.
    ///
    /// Short text is kept out entirely by <see cref="MinLengthForTolerance"/>, and stays out even
    /// though it churns the most: 86 of the sample's pairs were under 12 characters, "Yay!" read as
    /// "Yay" and "Yaya" and "Yav!" over and over. No ratio can separate those from "Yes" against
    /// "No", so they are a job for not recognising rubbish in the first place, not for tolerance.
    /// </remarks>
    public const double MaxRereadDifferenceRatio = 0.3;

    /// <summary>
    /// Whether these are two readings of one sentence rather than two sentences — close enough to
    /// be the same words, too far apart to be the same reading of them.
    /// </summary>
    /// <remarks>
    /// Recognition of an unchanged subtitle wobbles by more than <see cref="IsSameContent"/>
    /// tolerates surprisingly often: a lost space, a run of i/l confusions, a fragment ordered
    /// differently. Treated as a new sentence, each of those replaces a correct translation with a
    /// worse one — which is the reader's whole experience of "the OCR is unreliable", because the
    /// overlay shows the newest reading rather than the best one.
    /// </remarks>
    public static bool IsSameSentence(string a, string b)
    {
        if (IsSameContent(a, b)) return true;

        var left = NormaliseWhitespace(a);
        var right = NormaliseWhitespace(b);
        if (left.Length == 0 || right.Length == 0) return false;

        var longest = Math.Max(left.Length, right.Length);
        if (longest < MinLengthForTolerance) return false;

        var allowed = (int)(longest * MaxRereadDifferenceRatio);
        if (allowed < 1) return false;
        if (Math.Abs(left.Length - right.Length) > allowed) return false;

        return EditDistanceWithin(left, right, allowed);
    }

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
