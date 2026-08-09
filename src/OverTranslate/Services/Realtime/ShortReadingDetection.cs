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

    /// <summary>Length under which a reading has to be confident to be believed.</summary>
    /// <remarks>
    /// Ten, because that is where the measured samples separate: every reading of ten characters or
    /// more was a real line, and everything the model invented out of scenery was shorter.
    /// </remarks>
    public const int ShortTextLength = 10;

    /// <summary>Confidence a short reading needs before it is believed.</summary>
    /// <remarks>
    /// Measured over one subtitle session under PP-OCRv6 (2026-08-09, 11:12–11:14). Every short
    /// reading scoring below 0.80 was scenery, and every real short line scored well above it:
    ///
    /// <code>
    ///   0.60–0.79 (45 readings)  605G0  DE  ①  M'  NA  DM  'N  回  {02  316016  a06  米 …
    ///                            all noise, no exceptions
    ///   0.92–1.00                "Yay!" x5  "What?!"  "Why me?"  "0-0h, no!"  "月島まりな"
    ///                            all real subtitles
    /// </code>
    ///
    /// The gap between them is empty, so the floor sits in it rather than on a measured edge.
    /// Readings of <see cref="ShortTextLength"/> characters or more are never judged this way —
    /// the same session had real lines scoring as low as 0.68, and length is what tells them apart.
    /// </remarks>
    public const double ShortTextConfidenceFloor = 0.80;

    /// <summary>
    /// A short reading the recogniser was not sure about: what it returns when handed a picture.
    /// </summary>
    /// <remarks>
    /// Only possible to test for since PP-OCRv6. The model this replaced was every bit as confident
    /// about the single characters it read off scenery as about real text, which is why confidence
    /// was explicitly rejected as a signal earlier in this work — the change is in the model, not
    /// in the reasoning. <c>null</c> confidence means the engine reported no scores, and then this
    /// says nothing rather than guessing.
    /// </remarks>
    public static bool IsUnconvincingShortText(string? text, double? confidence) =>
        confidence is { } score
        && score < ShortTextConfidenceFloor
        && (text?.Trim().Length ?? 0) < ShortTextLength;
}
