namespace OverTranslate.Services.Providers;

/// <summary>Why a piece ended where it did, which is what decides how to put it back together.</summary>
internal enum TranslationChunkBoundary
{
    /// <summary>The end of the text: nothing follows.</summary>
    End,

    /// <summary>A full stop, question or exclamation mark that really ended a sentence.</summary>
    Sentence,

    /// <summary>A blank line — the only line break that reliably means a new paragraph.</summary>
    Paragraph,

    /// <summary>A space or a single line break, taken because no sentence ended in range.</summary>
    Whitespace,

    /// <summary>Nowhere to break at all, so the budget itself decided.</summary>
    HardSplit,
}

/// <summary>One piece of a text too long to send in a single request.</summary>
internal readonly record struct TranslationRequestChunk(
    string Text, TranslationChunkBoundary BoundaryAfter);

/// <summary>
/// Cuts a text too long for one request into pieces the free endpoints will accept, at the best
/// boundary each piece can reach.
/// </summary>
/// <remarks>
/// <para>The keyless endpoints take a thousand characters. Microsoft and Bing say so — GTranslate
/// refuses the call before it leaves the machine — but Google accepts the text and answers, and
/// what it answers past its own limit is not a translation. A 1,181-character paragraph came back
/// with its last sentence replaced by "僅在 GPU 10 秒上實現了 GPU 10 秒）" repeated seven times: the
/// model looping, not the text being cut off. Nothing in the pipeline could tell that from a
/// successful translation, because as far as every layer above is concerned it is one.</para>
///
/// <para>That silent failure is why the working budget is not the hard limit. A refusal is visible
/// and recoverable; a corrupted answer is neither, so the text is kept clear of the edge rather
/// than filled up to it — see <see cref="SafeMaxCharacters"/>.</para>
///
/// <para>This became reachable when a wrapped paragraph started going up as one request instead of
/// seven, which is the point of grouping and worth keeping. So the limit is answered where it
/// belongs — at the transport that has it — rather than by making the grouper translate less
/// context than the picture supports. Grouping decides what belongs together; this decides how much
/// of it an endpoint can safely be handed at once, and the two stay independent.</para>
/// </remarks>
internal static class TranslationRequestChunks
{
    /// <summary>What the endpoints refuse outright, and what this stays away from.</summary>
    public const int HardMaxCharacters = 1000;

    /// <summary>
    /// The most that is actually sent in one request.
    /// </summary>
    /// <remarks>
    /// A fifth under the hard limit, because the hard limit is where the <em>refusals</em> start and
    /// not where the answers stop being good. Google's repetition loop was measured at 1,181
    /// characters and nothing says where it begins; the endpoints are unpublished, may count
    /// encoded bytes rather than characters, and can change without notice. The cost of the margin
    /// is one extra request on a text between this and the hard limit, which is cheap next to a
    /// paragraph that comes back quietly wrong.
    /// </remarks>
    public const int SafeMaxCharacters = 800;

    public static List<TranslationRequestChunk> Split(string text, int limit = SafeMaxCharacters)
    {
        if (text.Length <= limit)
            return [new TranslationRequestChunk(text, TranslationChunkBoundary.End)];

        var chunks = new List<TranslationRequestChunk>();
        var start = 0;

        while (start < text.Length)
        {
            if (text.Length - start <= limit)
            {
                chunks.Add(new TranslationRequestChunk(text[start..], TranslationChunkBoundary.End));
                break;
            }

            var (length, boundary) = Cut(text, start, limit);
            chunks.Add(new TranslationRequestChunk(text[start..(start + length)], boundary));
            start += length;
        }

        return chunks;
    }

    /// <summary>
    /// How much of the text from <paramref name="start"/> to take, and what ended it.
    /// </summary>
    /// <remarks>
    /// <para>A sentence end and a blank line are both real boundaries, so the later of the two wins
    /// rather than the higher-ranked one: cutting at a full stop 300 characters in when a paragraph
    /// ends at 700 would throw away half the budget and buy nothing. A lone line break is not in
    /// that company — in a capture it is where the text met the edge of a column, and in pasted
    /// prose it is often the same. It ranks with an ordinary space.</para>
    ///
    /// <para>Where a sentence ends is a guess, and deliberately a cheap one: "Dr. Smith" and "e.g."
    /// will be read as sentence ends now and then. That is worth no parser and no dictionary,
    /// because the job is not to analyse the text — it is to find something better than a blind cut
    /// near the budget, and a break after "Dr." is still a break between words.</para>
    /// </remarks>
    private static (int Length, TranslationChunkBoundary Boundary) Cut(string text, int start, int limit)
    {
        var sentence = -1;
        var paragraph = -1;
        var whitespace = -1;

        for (var i = 0; i < limit; i++)
        {
            var at = start + i;

            if (IsSentenceEnd(text, at)) sentence = i + 1;
            else if (IsParagraphBreak(text, at)) paragraph = i + 1;
            else if (char.IsWhiteSpace(text[at])) whitespace = i + 1;
        }

        if (sentence > 0 || paragraph > 0)
        {
            var boundary = sentence >= paragraph
                ? TranslationChunkBoundary.Sentence
                : TranslationChunkBoundary.Paragraph;

            // Take the whitespace that follows with the sentence it belongs to, so no piece is sent
            // with a leading space or newline.
            return (TakeTrailingWhitespace(text, start, Math.Max(sentence, paragraph), limit), boundary);
        }

        if (whitespace > 0)
            return (whitespace, TranslationChunkBoundary.Whitespace);

        // A whole budget with no space and no full stop. Nothing to preserve, so the limit decides —
        // but never mid-character, which would send half a surrogate pair.
        return (
            char.IsLowSurrogate(text[start + limit]) ? limit - 1 : limit,
            TranslationChunkBoundary.HardSplit);
    }

    private static int TakeTrailingWhitespace(string text, int start, int length, int limit)
    {
        while (length < limit && char.IsWhiteSpace(text[start + length])) length++;
        return length;
    }

    /// <summary>
    /// Whether a sentence ends here, rather than the mark sitting inside "1.40s" or "PP-OCRv6".
    /// </summary>
    /// <remarks>
    /// Two lists rather than one rule, because the difference is not which language wrote the text
    /// — it is whether the mark has a second job. A line break is in neither, see <see cref="Cut"/>.
    /// </remarks>
    private static bool IsSentenceEnd(string text, int at) =>
        EndsSentenceAlone(text[at]) ||
        (EndsSentenceBeforeSpace(text[at]) &&
         (at + 1 >= text.Length || char.IsWhiteSpace(text[at + 1])));

    /// <summary>
    /// Marks that end a sentence and do nothing else, so they need no corroboration.
    /// </summary>
    /// <remarks>
    /// Written as the punctuation each script actually uses rather than as rules about the script,
    /// so adding one is adding a character and never a branch. Scripts that set no space after the
    /// mark are the reason this list exists at all: waiting for a space, as the list below does,
    /// would find no sentence end anywhere in Chinese, Japanese, Hindi or Urdu.
    /// </remarks>
    private static bool EndsSentenceAlone(char mark) =>
        mark is '。' or '！' or '？' or '｡' or '．'   // CJK, full-width and half-width
            or '۔' or '؟'                            // Arabic and Urdu
            or '।' or '॥'                            // Devanagari and the scripts that borrow it
            or '։' or '።';                           // Armenian and Ethiopic

    /// <summary>
    /// Marks that end a sentence only when something follows them, because they are also decimal
    /// points, abbreviations and mid-sentence pauses.
    /// </summary>
    private static bool EndsSentenceBeforeSpace(char mark) => mark is '.' or '!' or '?' or '…';

    /// <summary>Whether a blank line starts here, which is the one line break that means something.</summary>
    private static bool IsParagraphBreak(string text, int at)
    {
        if (text[at] is not '\n' and not '\r') return false;

        // Past this break, through any spaces on the empty line, looking for a second one.
        for (var i = at + 1; i < text.Length; i++)
        {
            if (text[i] is '\n') return true;
            if (text[i] is not ('\r' or ' ' or '\t')) return false;
        }

        return false;
    }

    /// <summary>
    /// Puts the translated pieces back together the way they were taken apart.
    /// </summary>
    /// <remarks>
    /// What separates two pieces is what separated them in the source, not a space by default: a
    /// blank line comes back as a blank line, a cut made mid-word closes up with nothing, and
    /// between sentences the two languages decide — Chinese, Japanese and Korean set their
    /// sentences without spaces, and inserting one puts a gap in the translation the original never
    /// had.
    /// </remarks>
    public static string Join(
        IReadOnlyList<TranslationRequestChunk> chunks, IReadOnlyList<string> translations)
    {
        var joined = new System.Text.StringBuilder();

        for (var i = 0; i < translations.Count; i++)
        {
            var part = translations[i].Trim();
            if (part.Length == 0) continue;

            if (joined.Length > 0)
                joined.Append(Separator(chunks[i - 1].BoundaryAfter, joined[^1], part[0]));

            joined.Append(part);
        }

        return joined.ToString();
    }

    private static string Separator(TranslationChunkBoundary boundary, char before, char after) =>
        boundary switch
        {
            TranslationChunkBoundary.Paragraph => "\n\n",
            TranslationChunkBoundary.HardSplit => "",
            _ => NeedsSpace(before, after) ? " " : "",
        };

    private static bool NeedsSpace(char before, char after) =>
        !char.IsWhiteSpace(before) && !char.IsWhiteSpace(after) &&
        !IsCjk(before) && !IsCjk(after);

    /// <summary>
    /// Whether a character belongs to a script that sets its words without spaces.
    /// </summary>
    /// <remarks>
    /// Enough of the ranges to answer the question this asks — Han, kana, Hangul, and the full-width
    /// forms that carry CJK punctuation. A character outside them is treated as one that wants
    /// spaces around it, which is right for every script the application translates into.
    /// </remarks>
    private static bool IsCjk(char character) =>
        character is >= '⺀' and <= '鿿'    // radicals, kana, bopomofo, Han
            or >= '가' and <= '힯'          // Hangul syllables
            or >= '豈' and <= '﫿'          // compatibility ideographs
            or >= '＀' and <= '￯';         // full-width forms and CJK punctuation
}
