namespace OverTranslate.Services.Ocr;

/// <summary>
/// The entries of a list, a table or a stat panel — the parts of a capture that repeat.
/// </summary>
/// <remarks>
/// <para>Every other test in <see cref="OcrTextBlockGrouper"/> asks one question of two lines:
/// could this be that one wrapping? A game's skill panel answers yes to all of them and is still
/// not a paragraph. Its entries are set at paragraph leading — measured 1.01 to 1.25 line
/// advances, inside the 0.67–1.24 that every real paragraph in the corpus occupies — flush to one
/// left edge, in one size, and their text carries no sentence boundary. So "Stun Power V+" over
/// "Health V+" clears the leading, the alignment, the size and the punctuation, and reaches the
/// translator as one invented phrase in one bubble.</para>
///
/// <para>What says they are separate is in neither line. It is that they are two of eight entries
/// that all start at the same x, sit the same distance apart, and each have a companion at the
/// same x further right — the level column. A wrapped paragraph has no companion column, and that
/// is what keeps this away from the sentences it must not touch.</para>
///
/// <para>Columns, not rows, are what this is built on. Two lists stand side by side often enough —
/// a game panel puts the party on the left and the selected character's skills on the right — and
/// then their entries interleave down the capture, so a scan in reading order finds neither: every
/// second entry breaks both the edge and the cadence. Worse, a row gathered across the full width
/// holds whatever else happened to share a baseline, so which box is first in it turns on a
/// coincidence — a stat panel's twelve skills survived the first version of this because a level
/// from the column beside them kept landing at the front of their rows.</para>
///
/// <para>Content is deliberately not consulted. The corpus is full of entries ending in "V+", "+"
/// or "Lv", and a rule written on those passes today's captures and nothing else; what generalises
/// is the repetition itself.</para>
/// </remarks>
internal sealed class RepeatedRowLayout
{
    /// <summary>No repeated entries — what a capture with nothing to detect gets.</summary>
    public static readonly RepeatedRowLayout None = new(new Dictionary<OcrTextBlock, int>());

    private readonly Dictionary<OcrTextBlock, int> _listOfEntry;

    private RepeatedRowLayout(Dictionary<OcrTextBlock, int> listOfEntry) =>
        _listOfEntry = listOfEntry;

    /// <summary>
    /// Whether these two lines are entries of one repeating list, and so cannot continue each
    /// other however much their geometry looks like a paragraph.
    /// </summary>
    public bool AreEntriesOfOneList(OcrTextBlock previous, OcrTextBlock current) =>
        _listOfEntry.Count > 0 &&
        _listOfEntry.TryGetValue(previous, out var previousList) &&
        _listOfEntry.TryGetValue(current, out var currentList) &&
        previousList == currentList;

    /// <summary>
    /// Finds every column of repeating entries in one capture.
    /// </summary>
    /// <param name="lines">
    /// The lines as they stand once the boxes sharing a row have been joined, which is the same
    /// thing the next-line test is about to judge. The companions that make an entry a row of
    /// something are still separate blocks here, because the space between a name and its level is
    /// far wider than any word space and the same-line merge leaves it alone.
    /// </param>
    public static RepeatedRowLayout Detect(IReadOnlyList<OcrTextBlock> lines)
    {
        if (lines.Count < MinimumEntries) return None;

        var columns = ByLeftEdge(lines);
        var listOfEntry = new Dictionary<OcrTextBlock, int>(
            (IEqualityComparer<OcrTextBlock>)ReferenceEqualityComparer.Instance);
        var lists = 0;

        foreach (var column in columns)
        {
            var start = 0;
            while (start <= column.Count - MinimumEntries)
            {
                var end = EndOfEvenlySpacedRun(column, start);
                var run = column.GetRange(start, end - start);

                if (run.Count >= MinimumEntries &&
                    AreShortEnoughToBeEntries(run) &&
                    HasCompanionColumn(run, columns))
                {
                    foreach (var entry in run) listOfEntry[entry] = lists;

                    lists++;
                    start = end;
                    continue;
                }

                // One entry on, not one run on: a run that failed only because a heading sits at
                // the top of it is still a list from its second entry down.
                start++;
            }
        }

        return listOfEntry.Count > 0 ? new RepeatedRowLayout(listOfEntry) : None;
    }

    /// <summary>The lines gathered into the columns they start from, each ordered down the page.</summary>
    private static List<List<OcrTextBlock>> ByLeftEdge(IReadOnlyList<OcrTextBlock> lines)
    {
        var columns = new List<List<OcrTextBlock>>();

        foreach (var line in lines.OrderBy(line => line.Bounds.X))
        {
            var tolerance = line.Bounds.Height * EdgeToleranceInLineHeights;
            var column = columns.FirstOrDefault(
                candidate => Math.Abs(candidate[^1].Bounds.X - line.Bounds.X) <= tolerance);

            if (column is null) columns.Add([line]);
            else column.Add(line);
        }

        foreach (var column in columns)
            column.Sort((left, right) => left.Bounds.Y.CompareTo(right.Bounds.Y));

        return columns;
    }

    /// <summary>How far down the column the cadence set by its first two entries holds.</summary>
    private static int EndOfEvenlySpacedRun(IReadOnlyList<OcrTextBlock> column, int start)
    {
        if (start + 1 >= column.Count) return start + 1;

        var pitch = column[start + 1].Bounds.Y - column[start].Bounds.Y;

        // A pitch far larger than the entries themselves is the distance across a layout rather
        // than the cadence of a list, and chaining on it would rope in whatever sits below.
        if (pitch <= 0 || pitch > column[start].Bounds.Height * MaximumPitchInLineHeights)
            return start + 1;

        var end = start + 1;
        while (end < column.Count &&
               Math.Abs(column[end].Bounds.Y - column[end - 1].Bounds.Y - pitch) <=
                   pitch * PitchTolerance)
            end++;

        return end;
    }

    /// <summary>
    /// Whether the run holds about as much text as an entry rather than as much as a line of prose.
    /// </summary>
    /// <remarks>
    /// <para>The test that tells a list from text set in columns, and it is not optional. A
    /// documentation site's front page sets its articles in cards side by side, so one card's lines
    /// share an edge, keep a cadence, and have the other card's lines beside them at a shared x —
    /// every signal here, from something that is four wrapped paragraphs. It tore all four apart
    /// until this was added.</para>
    ///
    /// <para>The bar is the one <c>OcrTextBlockGrouper.WrappedLineMinAspect</c> already uses to ask
    /// whether a line could have run out of room, and for the same reason: below it nothing was
    /// filled, so nothing about the line is a wrap. Measured, the two populations are far apart —
    /// card prose runs 16 to 26 times its own height, game panel entries 3.6 to 6.0.</para>
    ///
    /// <para>The median, so that one long entry among short ones does not decide it and neither
    /// does one short line ending a paragraph.</para>
    /// </remarks>
    private static bool AreShortEnoughToBeEntries(IReadOnlyList<OcrTextBlock> run)
    {
        var aspects = run
            .Select(entry => entry.Bounds.Width / Math.Max(1, entry.Bounds.Height))
            .OrderBy(aspect => aspect)
            .ToList();

        return aspects[aspects.Count / 2] < MaximumEntryAspect;
    }

    /// <summary>
    /// Whether most of the run's entries have a companion beside them, all starting at one x — the
    /// level, the amount or the count that makes an entry a row of something.
    /// </summary>
    private static bool HasCompanionColumn(
        IReadOnlyList<OcrTextBlock> run, IReadOnlyList<List<OcrTextBlock>> columns)
    {
        var needed = Math.Max(
            MinimumEntries, (int)Math.Ceiling(run.Count * MinimumCompanionCoverage));

        foreach (var column in columns)
        {
            var companions = run.Count(
                entry => column.Any(candidate => IsCompanionOf(entry, candidate)));

            if (companions >= needed) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether one block sits beside another rather than under it: on its row, and clear of it
    /// horizontally, so that it is a second column and not the same one read twice.
    /// </summary>
    private static bool IsCompanionOf(OcrTextBlock entry, OcrTextBlock candidate)
    {
        if (ReferenceEquals(entry, candidate)) return false;

        if (candidate.Bounds.Left < entry.Bounds.Right &&
            candidate.Bounds.Right > entry.Bounds.Left)
            return false;

        var overlap =
            Math.Min(entry.Bounds.Bottom, candidate.Bounds.Bottom) -
            Math.Max(entry.Bounds.Top, candidate.Bounds.Top);

        return overlap >= Math.Min(entry.Bounds.Height, candidate.Bounds.Height) * MinimumRowOverlap;
    }

    /// <summary>
    /// How many entries a run needs. Three, because two is what a wrapped sentence has.
    /// </summary>
    private const int MinimumEntries = 3;

    /// <summary>How far an entry's left edge may wander and still be one column, in line heights.</summary>
    private const double EdgeToleranceInLineHeights = 0.6;

    /// <summary>How much the distance between entries may vary and still be a cadence.</summary>
    private const double PitchTolerance = 0.25;

    /// <inheritdoc cref="EndOfEvenlySpacedRun"/>
    private const double MaximumPitchInLineHeights = 3.0;

    /// <inheritdoc cref="AreShortEnoughToBeEntries"/>
    private const double MaximumEntryAspect = 8.0;

    /// <summary>
    /// How many of a run's entries must have a companion. Not all of them: the corpus has entries
    /// whose level was read as part of the name beside it, and one such entry in the middle of
    /// eight must not hide the column the other seven share.
    /// </summary>
    private const double MinimumCompanionCoverage = 0.6;

    /// <summary>How much of the shorter block must share the taller one's rows to be beside it.</summary>
    private const double MinimumRowOverlap = 0.5;
}
