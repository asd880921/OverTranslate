namespace OverTranslate.Services.Providers;

/// <summary>
/// Cuts a text too long for one request into pieces the free endpoints will accept, at sentence
/// ends wherever there is one to cut at.
/// </summary>
/// <remarks>
/// <para>The keyless endpoints take a thousand characters. Microsoft and Bing say so — GTranslate
/// refuses the call before it leaves the machine — but Google accepts the text and answers, and
/// what it answers past its own limit is not a translation. A 1,181-character paragraph came back
/// with its last sentence replaced by "僅在 GPU 10 秒上實現了 GPU 10 秒）" repeated seven times: the
/// model looping, not the text being cut off. Nothing in the pipeline could tell that from a
/// successful translation, because as far as every layer above is concerned it is one.</para>
///
/// <para>This became reachable when a wrapped paragraph started going up as one request instead of
/// seven, which is the point of grouping and worth keeping. So the limit is answered where it
/// belongs — at the transport that has it — rather than by making the grouper translate less
/// context than the picture supports.</para>
///
/// <para>Sentence ends are preferred over word boundaries and word boundaries over nothing, because
/// what a piece costs is the context inside it: a sentence split down the middle is translated as
/// two half-thoughts, while consecutive whole sentences lose only what sits between them.</para>
/// </remarks>
internal static class TranslationRequestChunks
{
    /// <summary>
    /// The longest text the keyless endpoints accept, and the smallest of their limits, because a
    /// block is offered to whichever of them answers first.
    /// </summary>
    public const int MaxCharacters = 1000;

    public static List<string> Split(string text, int limit = MaxCharacters)
    {
        if (text.Length <= limit) return [text];

        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            if (text.Length - start <= limit)
            {
                chunks.Add(text[start..]);
                break;
            }

            var length = CutLength(text, start, limit);
            chunks.Add(text[start..(start + length)]);
            start += length;
        }

        return chunks;
    }

    /// <summary>
    /// How much of the text starting at <paramref name="start"/> to take: as far as the last
    /// sentence end within the limit, else the last word boundary, else the limit itself.
    /// </summary>
    private static int CutLength(string text, int start, int limit)
    {
        var lastSentenceEnd = -1;
        var lastBoundary = -1;

        for (var i = 0; i < limit; i++)
        {
            var at = start + i;
            if (IsSentenceEnd(text, at)) lastSentenceEnd = i + 1;
            else if (char.IsWhiteSpace(text[at])) lastBoundary = i + 1;
        }

        // Take the space after the full stop with the sentence it follows, so no piece is sent
        // with a leading space.
        if (lastSentenceEnd > 0)
        {
            while (lastSentenceEnd < limit && char.IsWhiteSpace(text[start + lastSentenceEnd]))
                lastSentenceEnd++;

            return lastSentenceEnd;
        }

        if (lastBoundary > 0) return lastBoundary;

        // A thousand characters without a space or a full stop. Nothing to preserve, so take the
        // limit — but never mid-character, which would send half a surrogate pair.
        return char.IsLowSurrogate(text[start + limit]) ? limit - 1 : limit;
    }

    /// <summary>
    /// Whether a full stop here ends a sentence rather than sitting inside "1.40s" or "PP-OCRv6".
    /// </summary>
    /// <remarks>
    /// The Latin marks need what follows to be a space, because they are also decimal points and
    /// abbreviations; the CJK ones do not, because nothing else uses them.
    /// </remarks>
    private static bool IsSentenceEnd(string text, int at) =>
        text[at] switch
        {
            '。' or '！' or '？' or '\n' => true,
            '.' or '!' or '?' => at + 1 >= text.Length || char.IsWhiteSpace(text[at + 1]),
            _ => false,
        };

    /// <summary>
    /// Puts the pieces back together, with a space wherever both sides are words rather than CJK.
    /// </summary>
    public static string Join(IEnumerable<string> translations)
    {
        var joined = "";

        foreach (var part in translations.Select(part => part.Trim()).Where(part => part.Length > 0))
        {
            if (joined.Length == 0)
            {
                joined = part;
                continue;
            }

            var needsSpace = !char.IsWhiteSpace(joined[^1]) && !char.IsWhiteSpace(part[0]);
            joined = needsSpace ? $"{joined} {part}" : joined + part;
        }

        return joined;
    }
}
