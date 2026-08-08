namespace OverTranslate.Services.Realtime;

/// <summary>
/// Recognises a reading too short to be a line of text: a box the recogniser got one character out
/// of, which is what a detector finds when it is looking at picture rather than words.
/// </summary>
/// <remarks>
/// The same damage <see cref="CollapsedDetection"/> exists to stop, arriving by the other route.
/// That one tests the shape of the box and catches a detector that threw a single box across the
/// whole block; this catches the small boxes it lands on scenery, which are nowhere near tall
/// enough to look like a collapse and read as one character just the same.
///
/// Measured over a 2 minute 30 second subtitle session (2026-08-09, region 1699x242). The detector
/// was asked for a second size on five passes that read nothing at the first; three of those five
/// were frames with no subtitle on screen at all — a wooden floor, a table, a character's back —
/// and each returned exactly one character:
///
/// <code>
///   01:07:46  chars=1   no subtitle (table)        overwrote 33 characters on screen
///   01:08:37  chars=1   no subtitle (floor)
///   01:08:40  chars=1   no subtitle (floor)        overwrote 26 characters on screen
///   01:08:01  chars=20  "It's a bit too vague."
///   01:09:14  chars=14  "It might just..."
/// </code>
///
/// One character against fourteen and twenty: the two kinds separate with nothing in between, which
/// is why the floor sits at <see cref="MinimumCharacters"/> and not at some fraction of the usual
/// reading. The cost of missing them is not a wasted inference — a one-character reading looks
/// nothing like the subtitle it replaced, so it counts as a new sentence and puts a single glyph on
/// screen where a correct translation was.
///
/// Deliberately not a confidence test. The recogniser was perfectly confident about those single
/// characters; it really did see that shape. What is wrong with them is that one character is not a
/// subtitle, and length is the thing that says so.
/// </remarks>
internal static class ShortReadingDetection
{
    /// <summary>
    /// Characters a reading needs before it is treated as text. Two, because every measured false
    /// reading was one character and the shortest real one was fourteen — and because a genuine
    /// single character (a lone "?" between lines) carries nothing worth translating anyway.
    /// </summary>
    public const int MinimumCharacters = 2;

    public static bool IsTooShort(string? text) =>
        (text?.Trim().Length ?? 0) < MinimumCharacters;
}
