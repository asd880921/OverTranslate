namespace OverTranslate.Services.Realtime;

/// <summary>
/// Learns how tall the text in one watched region is, and rejects what is far too tall to be part
/// of it.
/// </summary>
/// <remarks>
/// The detector sometimes returns one enormous box spanning most of a region, and the recogniser
/// reads a single letter out of the stretched crop it gets. Measured over one session: 1263 accepted
/// blocks, of which 93 were three characters or fewer, and those were almost entirely noise — lone
/// A, M, □, 米, ✨, brackets, single digits. Four of them arrived in boxes 194 to 343 pixels tall
/// where the region's real lines were 86, and they did real damage: a one-character reading differs
/// from the subtitle on screen by far more than <see cref="TextSimilarity"/> forgives, so it counted
/// as a new sentence, was translated, and replaced a correct translation with a giant "A".
///
/// Confidence cannot sort this out — the noise scored between 0.60 and 0.99, overlapping the median
/// of 0.95 for real lines — and neither can length alone, because "YA" is a real subtitle. What does
/// sort it out is that this region has been watched for a while and its text has a size: the real
/// "YA" arrived in a box the same height as every other line, and the noise arrived two to four
/// times taller.
///
/// Nothing like this exists for the screenshot flow, which sees each capture once and has no idea
/// what size its text should be.
/// </remarks>
internal sealed class RegionTextScale
{
    /// <summary>
    /// Lines to remember. Long enough to survive a few odd readings, short enough to follow content
    /// that genuinely changes size — a player moving from subtitles to a menu.
    /// </summary>
    public const int Window = 16;

    /// <summary>
    /// How many lines must be known before anything is rejected. Until then the region is taken at
    /// face value: refusing text on the strength of one or two samples would be worse than the
    /// occasional oversized reading.
    /// </summary>
    public const int MinSamples = 4;

    /// <summary>
    /// How much taller than the region's usual line a block may be before it is treated as a
    /// misdetection rather than text. The measured noise starts at 2.1x, and real lines in one
    /// region vary by a few percent, so this sits below the first and well above the second.
    /// </summary>
    public const double MaxHeightAboveUsual = 1.8;

    /// <summary>
    /// Only lines this long are learned from. Shorter boxes are exactly the ones under suspicion,
    /// and letting them into the sample would teach the region that giant boxes are normal.
    /// </summary>
    public const int MinGlyphsToLearnFrom = 4;

    private readonly Queue<double> _heights = new();

    /// <summary>The usual line height, or null while too little has been seen to say.</summary>
    public double? UsualHeight
    {
        get
        {
            if (_heights.Count < MinSamples) return null;

            // Median rather than mean: one 343-pixel misdetection that slipped through should not
            // drag the yardstick up far enough to admit the next one.
            var sorted = _heights.OrderBy(height => height).ToList();
            return sorted[sorted.Count / 2];
        }
    }

    public void Observe(double height, int glyphCount)
    {
        if (height <= 0 || glyphCount < MinGlyphsToLearnFrom) return;

        _heights.Enqueue(height);
        while (_heights.Count > Window) _heights.Dequeue();
    }

    /// <summary>Whether this box is too tall to be a line of this region's text.</summary>
    public bool IsOversized(double height) =>
        UsualHeight is { } usual && height > usual * MaxHeightAboveUsual;
}
