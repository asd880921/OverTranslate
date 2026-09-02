namespace OverTranslate.Services.Ocr;

/// <summary>
/// Where the line falls between a space that separates words and a space that separates things,
/// for one capture, measured in line heights.
/// </summary>
/// <remarks>
/// <para>The distance is not a property of the application — it is a property of the picture. A
/// documentation site sets its navigation 0.6 of a line apart and its prose 0.3; a dense panel may
/// run 0.15 and 0.35. One fixed number cannot be right for both, and being wrong in the merging
/// direction is expensive: eleven menu entries glued into one string change what the translator is
/// asked, while a sentence left in two halves is only what the previous version already did.</para>
///
/// <para>So the gaps a capture actually contains are measured, split in two, and the line drawn
/// between the halves. When they do not fall into two convincing halves — too few of them, one side
/// nearly empty, the two sides touching, or an answer outside the range any real spacing occupies —
/// this returns <see cref="Fallback"/> and says why. That number is the measured one from the
/// screenshot corpus and remains correct whenever the picture cannot say better.</para>
/// </remarks>
internal readonly record struct SameLineGapThreshold(double Value, bool Adaptive, string Reason)
{
    /// <summary>
    /// The threshold used whenever a capture cannot supply a more convincing one.
    /// </summary>
    /// <remarks>
    /// Measured across the screenshot corpus: word gaps inside one real line run from -0.18 to 0.38
    /// (the nine in "Send this session to the background and free the terminal", and every same-row
    /// pair the tests were built from), while a documentation site's eleven navigation entries run
    /// 0.54 to 0.70 and two cards side by side 1.11. Nothing at all lies between 0.38 and 0.54, so
    /// this sits in the middle of that empty band rather than against either edge.
    /// </remarks>
    public const double Fallback = 0.45;

    /// <summary>Gaps outside this are not spacing decisions and are left out of the measurement.</summary>
    /// <remarks>
    /// Below the floor the boxes overlap further than the detector's expansion explains; above the
    /// ceiling is the distance across a layout rather than between neighbours, and a handful of
    /// those would drag the loose half so far out that the midpoint between the two halves lands
    /// nowhere near any real spacing.
    /// </remarks>
    private const double SampleFloor = -1.0;

    /// <inheritdoc cref="SampleFloor"/>
    private const double SampleCeiling = 2.0;

    /// <summary>How many gaps a capture must show before its own spacing is believed.</summary>
    private const int MinimumSamples = 6;

    /// <summary>How many gaps each half needs before it counts as a population rather than a stray.</summary>
    private const int MinimumHalfSize = 2;

    /// <summary>
    /// How far apart the two halves must sit, in line heights, to be two kinds of space rather than
    /// one kind unevenly measured.
    /// </summary>
    /// <remarks>
    /// The measured band between word spacing and item spacing is 0.16 wide (0.38 to 0.54). Asking
    /// for that much again means a capture whose gaps merely spread out — prose alone, or a menu
    /// alone — cannot produce a split, because the widest run of one kind of space seen so far does
    /// not contain a hole this big.
    /// </remarks>
    private const double MinimumSeparation = 0.15;

    /// <summary>The range a believable answer falls in.</summary>
    /// <remarks>
    /// Bounded on both sides rather than clamped into: an estimate outside this is not a slightly
    /// wrong threshold to be pulled back, it is evidence that the two halves were not the two kinds
    /// of space this is looking for, and the fallback is the better answer.
    /// </remarks>
    private const double MinimumThreshold = 0.25;

    /// <inheritdoc cref="MinimumThreshold"/>
    private const double MaximumThreshold = 0.55;

    public static SameLineGapThreshold Estimate(IReadOnlyList<double> gaps)
    {
        var samples = gaps
            .Where(gap => gap is >= SampleFloor and <= SampleCeiling)
            .OrderBy(gap => gap)
            .ToList();

        if (samples.Count < MinimumSamples)
            return FallbackWith("too few gaps");

        var split = BestSplit(samples);
        if (split < 0)
            return FallbackWith("no two-sided split");

        var tightest = samples[split - 1];
        var loosest = samples[split];
        if (loosest - tightest < MinimumSeparation)
            return FallbackWith("halves not separated");

        var threshold = (tightest + loosest) / 2;
        return threshold is < MinimumThreshold or > MaximumThreshold
            ? FallbackWith("outside the believable range")
            : new SameLineGapThreshold(threshold, true, "measured on this capture");
    }

    private static SameLineGapThreshold FallbackWith(string reason) => new(Fallback, false, reason);

    /// <summary>
    /// Where a sorted list of gaps divides into two groups, or -1 when it cannot be divided.
    /// </summary>
    /// <remarks>
    /// Every place the sorted list could be cut is tried and the one leaving the least spread
    /// within the two halves wins. In one dimension that is exhaustive rather than iterative, which
    /// makes it both optimal and the same answer every time — where k-means would hand back
    /// whatever its starting points led to, and a test would be pinning that instead of the data.
    /// The list is short enough (the gaps of one screenshot) that trying them all costs nothing.
    /// </remarks>
    private static int BestSplit(List<double> sorted)
    {
        var best = -1;
        var bestSpread = double.PositiveInfinity;

        for (var split = MinimumHalfSize; split <= sorted.Count - MinimumHalfSize; split++)
        {
            var spread = Spread(sorted, 0, split) + Spread(sorted, split, sorted.Count);
            if (spread >= bestSpread) continue;

            bestSpread = spread;
            best = split;
        }

        return best;
    }

    /// <summary>Summed squared distance from the mean, over one half.</summary>
    private static double Spread(List<double> sorted, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += sorted[i];

        var mean = sum / (to - from);
        double spread = 0;
        for (var i = from; i < to; i++) spread += (sorted[i] - mean) * (sorted[i] - mean);

        return spread;
    }
}
