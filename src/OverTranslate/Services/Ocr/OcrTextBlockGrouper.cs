using System.Diagnostics;
using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Joins the lines the engine read into the blocks that get translated together.
/// </summary>
/// <remarks>
/// Every measurement a decision here is made on comes from <c>LayoutBounds</c>, the box the
/// detector drew, and never from <c>Bounds</c>. Bounds has been normalised per script by the time
/// it arrives — a CJK line is pulled in onto its glyphs and a Latin one is left whole — so reading
/// it made every threshold mean something different depending on which language the user had
/// selected. The horizontal figures are the same in both (normalisation only touches Y and height);
/// the vertical ones are not, and neither is anything derived from a line height.
///
/// What the group carries out is still Bounds: that is the area the overlay has to cover.
/// </remarks>
internal static class OcrTextBlockGrouper
{
    /// <param name="profile">
    /// The thresholds this pass runs on. Required rather than defaulted: which profile a call
    /// means is the one thing about it that must not be inherited by accident, and the screenshot
    /// and live-screen paths are supposed to be able to diverge (see <see cref="GroupingProfile"/>).
    /// </param>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks, GroupingProfile profile) => Group(blocks, profile, null);

    /// <param name="decisions">
    /// Collects one entry per line pair the next-line test was asked about, with the geometry it
    /// judged on and which rule decided. Null in the app; OcrHarness passes a list, because these
    /// thresholds cannot be tuned from the grouped output alone — it shows what was joined, never
    /// how close the rest came to being.
    /// </param>
    /// <inheritdoc cref="Group(IReadOnlyList{OcrTextBlock}, GroupingProfile)" path="/param[@name='profile']"/>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks,
        GroupingProfile profile,
        List<NextLineDecision>? decisions)
    {
        AssertLayoutGeometryFilled(blocks);

        if (blocks.Count <= 1)
            return blocks.ToList();

        var sameLineMerged = MergeSameLineFragments(blocks, decisions);
        var sorted = sameLineMerged
            .OrderBy(block => block.LayoutBounds.Y)
            .ThenBy(block => block.LayoutBounds.X)
            .ToList();

        var groups = new List<List<OcrTextBlock>>();
        foreach (var block in sorted)
        {
            var target = GroupThisLineContinues(groups, block, sorted, profile, decisions);
            if (target is not null)
                target.Add(block);
            else
                groups.Add([block]);
        }

        return groups.Select(BuildGroup).ToList();
    }

    /// <summary>
    /// Every block reaching the grouper must carry the detection box the layout tests measure on.
    /// </summary>
    /// <remarks>
    /// Loud on purpose, and deliberately not a fallback to <c>Bounds</c>. A block that arrives with
    /// nothing in LayoutBounds is a construction path that forgot to fill it, and quietly reading
    /// Bounds instead would put the script-dependent geometry — the 0.820 this whole split exists
    /// to remove — back into grouping without anything saying so. Compiled out of Release, so a
    /// degenerate detection box cannot take the app down; the tests run Debug and will not miss it.
    /// </remarks>
    [Conditional("DEBUG")]
    private static void AssertLayoutGeometryFilled(IReadOnlyList<OcrTextBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.LayoutBounds.Width > 0 && block.LayoutBounds.Height > 0)
                continue;

            throw new InvalidOperationException(
                $"Block \"{block.Text}\" reached the grouper with no LayoutBounds " +
                $"({block.LayoutBounds}). Fill it where the block is built.");
        }
    }

    /// <summary>
    /// One grouping verdict, with its geometry in line heights so numbers from captures of
    /// different sizes can be read side by side.
    /// </summary>
    /// <remarks>
    /// TWO KINDS SHARE THESE FIELDS AND DO NOT FILL THEM WITH THE SAME QUANTITIES. Anything
    /// aggregating a run has to split on <c>Kind</c> first; pooling the two mixes a horizontal
    /// measurement into a vertical distribution and reads as a pile of zeroes at one end.
    ///
    /// <code>
    ///   field           Kind "next" (line below)        Kind "row" (same line, left to right)
    ///   VerticalGap     vertical gap between lines      HORIZONTAL gap between neighbours
    ///   LeftDelta       left-edge misalignment          vertical overlap rate, 0..1
    ///   CenterDelta     centre misalignment             unused, 0
    ///   RightDelta      right-edge misalignment         unused, 0
    ///   AlignmentDelta  the smallest of those three,    unused, 0
    ///                   which is what the gate read
    ///   TextSizeRatio   glyph or box height ratio       unused, 0
    ///   LineAdvance     baseline advance                unused, 0
    ///   LeadingBar      the advance bar it was judged   unused, 0
    ///                   against                         
    ///   SolidBar        the set-solid limit this group  unused, 0
    ///                   was held to                     
    ///   WidthRatio      width ratio                     width ratio
    /// </code>
    ///
    /// Renaming the fields per kind means two records and two code paths through the grouper for
    /// what is one diagnostic; naming them for the vertical case and documenting the reuse is the
    /// cheaper honest option. <c>OcrHarness --group-explain</c> prints each kind with its own
    /// labels, and must keep doing so.
    /// </remarks>
    /// <param name="Kind">"next" for a line-below verdict, "row" for a same-line one.</param>
    /// <param name="Rule">Which test decided, whether it joined or refused.</param>
    internal readonly record struct NextLineDecision(
        string Kind,
        string Previous,
        string Current,
        OcrLayoutScript PreviousScript,
        OcrLayoutScript CurrentScript,
        double VerticalGap,
        double LeftDelta,
        double CenterDelta,
        double RightDelta,
        double AlignmentDelta,
        double TextSizeRatio,
        double WidthRatio,
        double LineAdvance,
        double LeadingBar,
        double SolidBar,
        bool Joined,
        string Rule);

    private static List<OcrTextBlock> MergeSameLineFragments(
        IReadOnlyList<OcrTextBlock> blocks, List<NextLineDecision>? decisions)
    {
        var rows = BuildVisualRows(blocks);
        var gaps = rows.SelectMany(AdjacentGaps).ToList();
        var threshold = SameLineGapThreshold.Estimate(gaps);

        return rows.SelectMany(row => SplitRowIntoLines(row, threshold.Value, decisions)).ToList();
    }

    /// <summary>
    /// Gathers the boxes that sit on one line of the picture, left to right, whatever the spaces
    /// between them turn out to mean.
    /// </summary>
    /// <remarks>
    /// Membership only, with no gap test. Which spaces are wide enough to separate things cannot be
    /// decided one pair at a time, because it depends on the spacing this particular capture uses,
    /// and that cannot be measured until the rows are known. So the rows are built first and cut
    /// afterwards.
    ///
    /// Sorted by X and not by Y. Latin word boxes on one line have tops that vary with ascenders
    /// and descenders (a real line gave "Send" at y=32 beside "to" at y=37), so a Y-primary sort
    /// interleaves words from across the capture and a row built in that order ends up holding
    /// fragments of several. Reading order keeps a line's own boxes together, and the vertical
    /// overlap test routes each box to the right row when several are open at once.
    /// </remarks>
    private static List<List<OcrTextBlock>> BuildVisualRows(IReadOnlyList<OcrTextBlock> blocks)
    {
        var ordered = blocks
            .OrderBy(block => block.LayoutBounds.X)
            .ThenBy(block => block.LayoutBounds.Y)
            .ToList();
        var rows = new List<List<OcrTextBlock>>();

        foreach (var block in ordered)
        {
            var bestRow = -1;
            var bestOverlap = double.NegativeInfinity;

            for (var i = 0; i < rows.Count; i++)
            {
                // Against the rightmost box of the row, which is the one this would follow.
                var rightmost = rows[i][^1];
                if (!SharesVisualRow(rightmost, block)) continue;

                var overlap = VerticalOverlapRate(rightmost, block);
                if (overlap <= bestOverlap) continue;

                bestOverlap = overlap;
                bestRow = i;
            }

            if (bestRow >= 0) rows[bestRow].Add(block);
            else rows.Add([block]);
        }

        return rows;
    }

    /// <summary>Whether two boxes sit on the same line of the picture. Says nothing about joining.</summary>
    private static bool SharesVisualRow(OcrTextBlock previous, OcrTextBlock current)
    {
        // Vertical overlap — NOT height ratio — is what tells an in-line word from a distinct
        // neighbour. A short mid-line word ("to" h=25 on a h=31 line, heightRatio 0.81) sits on the
        // same baseline, so its box is fully nested in the line (overlap about 1.0). Two stacked
        // buttons ("Download…report" h=32 next to a vertically offset "Create key" h=38,
        // heightRatio 0.84) overlap only about 0.47. Their height ratios are inverted
        // (0.81 < 0.84), so any single height threshold either drops "to" or merges the buttons.
        // Keep height tolerant and let the strict overlap test do the discriminating.
        //
        // 0.5 rather than 0.6, measured: a subtitle reading "Let us pay CiRCLE a visit on the way
        // home." came back as an 888x88 box and a 141x51 one holding "home", a height ratio of
        // 0.58. They sit 2px apart with the shorter box entirely inside the taller one's rows, and
        // the guard rejected them anyway, so "home" was translated on its own and appeared as a
        // stray word to the right of a sentence missing its ending. The word's box is short because
        // the word has no descender, which is a property of the letters, not of whether they belong
        // to the line.
        var heightRatio = Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) /
                          Math.Max(previous.LayoutBounds.Height, current.LayoutBounds.Height);

        return heightRatio >= MinimumRowHeightRatio &&
               VerticalOverlapRate(previous, current) >= MinimumRowVerticalOverlap;
    }

    private static double VerticalOverlapRate(OcrTextBlock previous, OcrTextBlock current)
    {
        var overlap = Math.Max(
            0,
            Math.Min(previous.LayoutBounds.Bottom, current.LayoutBounds.Bottom) -
            Math.Max(previous.LayoutBounds.Top, current.LayoutBounds.Top));

        return overlap /
               Math.Max(1, Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height));
    }

    /// <summary>The space before each box in a row, in line heights, neighbour to neighbour.</summary>
    private static IEnumerable<double> AdjacentGaps(List<OcrTextBlock> row) =>
        row.Zip(row.Skip(1), NormalizedGap);

    private static double NormalizedGap(OcrTextBlock previous, OcrTextBlock current) =>
        (current.LayoutBounds.X - previous.LayoutBounds.Right) /
        Math.Max(1, (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0);

    /// <summary>
    /// Cuts one row wherever the space between neighbours is too wide to be a space between words,
    /// and joins what is left.
    /// </summary>
    private static IEnumerable<OcrTextBlock> SplitRowIntoLines(
        List<OcrTextBlock> row, double threshold, List<NextLineDecision>? decisions)
    {
        var lines = new List<OcrTextBlock>();
        var line = row[0];

        for (var i = 1; i < row.Count; i++)
        {
            var (joined, rule) = JudgeSameLine(row[i - 1], row[i], threshold);
            decisions?.Add(SameLineDecision(row[i - 1], row[i], joined, rule));

            if (joined)
            {
                line = MergeSameLine(line, row[i]);
                continue;
            }

            lines.Add(line);
            line = row[i];
        }

        lines.Add(line);
        return lines;
    }

    private static (bool Joined, string Rule) JudgeSameLine(
        OcrTextBlock previous, OcrTextBlock current, double threshold)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        // The gap can be negative: on large captures the detector's unclip expansion enlarges big
        // heading word-boxes until adjacent ones overlap horizontally (e.g. "Translate" right=533
        // vs "your website" left=515, a gap of -18), which a non-negative guard rejected, scattering
        // one heading into word-by-word translations. Allow up to a line-height of overlap; the
        // checks in SharesVisualRow already keep stacked lines out.
        var horizontalGap = current.LayoutBounds.X - previous.LayoutBounds.Right;
        if (horizontalGap < -avgHeight)
            return (false, "overlaps too far");

        // The floor is for text too small for a ratio to mean much. It was 18px, which at the sizes
        // it applied to was itself wide enough to cross a menu.
        return horizontalGap <= Math.Max(avgHeight * threshold, MinimumRowGapPixels)
            ? (true, "same row")
            : (false, "horizontal gap");
    }

    /// <summary>
    /// How close in text size two lines have to be before one can be the other wrapping.
    /// </summary>
    /// <remarks>
    /// Named rather than written into the test because <see cref="GroupingProfile"/> quotes it: a
    /// profile that says "the ordinary figure" has to be equal to the ordinary figure, and two
    /// copies of 0.88 in two files is one of them being moved on its own eventually.
    /// </remarks>
    internal const double MinTextSizeRatio = 0.88;

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowHeightRatio = 0.5;

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowVerticalOverlap = 0.72;

    /// <inheritdoc cref="JudgeSameLine"/>
    private const double MinimumRowGapPixels = 6;

    private static NextLineDecision SameLineDecision(
        OcrTextBlock previous, OcrTextBlock current, bool joined, string rule) =>
        new("row",
            previous.Text,
            current.Text,
            previous.LayoutScript,
            current.LayoutScript,
            NormalizedGap(previous, current),
            VerticalOverlapRate(previous, current),
            // Centre, right, the alignment figure taken from them, size, advance and the leading
            // bar are vertical quantities. A same-line verdict has no such thing to report, so they
            // stay at zero and the harness does not print them for this kind.
            0,
            0,
            0,
            0,
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
            0,
            0,
            0,
            joined,
            rule);

    private static OcrTextBlock MergeSameLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var left = Math.Min(previous.Bounds.Left, current.Bounds.Left);
        var top = Math.Min(previous.Bounds.Top, current.Bounds.Top);
        var right = Math.Max(previous.Bounds.Right, current.Bounds.Right);
        var bottom = Math.Max(previous.Bounds.Bottom, current.Bounds.Bottom);
        var text = JoinInlineText(previous.Text, current.Text);
        var layoutScript = LayoutScriptDetection.For(text);

        return new OcrTextBlock(
            text,
            new Rect(left, top, right - left, bottom - top),
            RenderGlyphHeight: CombineGlyphHeight(previous.RenderGlyphHeight, current.RenderGlyphHeight),
            Confidence: CombineConfidence([previous, current]),
            LayoutScript: layoutScript,
            LayoutBounds: Rect.Union(previous.LayoutBounds, current.LayoutBounds),
            LayoutGlyphHeight: CombineLayoutGlyphHeight(layoutScript, [previous, current]));
    }

    private static string JoinInlineText(string left, string right)
    {
        left = left.TrimEnd();
        right = right.TrimStart();
        if (left.Length == 0)
            return right;
        if (right.Length == 0)
            return left;

        var needsSpace = char.IsAsciiLetterOrDigit(left[^1]) && char.IsAsciiLetterOrDigit(right[0]);
        return needsSpace ? $"{left} {right}" : $"{left}{right}";
    }

    /// <summary>
    /// Which of the groups opened so far this line carries on, or null if it starts a new one.
    /// </summary>
    /// <remarks>
    /// <para>This used to ask only the group opened last, which is the right question for a single
    /// column and the wrong one for anything else. Reading order down a two-column page alternates
    /// between the columns, so a line's own previous line is often not the one immediately before it
    /// in this list — a Japanese event page has a title whose two halves sit at (1340,612) and
    /// (1339,650), one pixel apart on the left and about one line advance down, with a heading from
    /// the next column at (1912,615) sorted between them. That pair was never refused: it was never
    /// asked about at all.</para>
    ///
    /// <para>Asking every open group means several may answer yes, so the choice between them is
    /// fixed here rather than left to whichever the loop happened to see first. The order is part of
    /// the rule: closest alignment wins; a tie inside a hundredth of a line height goes to the
    /// vertically nearer; a tie there goes to the more recently opened group, which is the scan
    /// order. The last of those settles nothing on the evidence — it is there so that the same
    /// input always produces the same grouping.</para>
    ///
    /// <para>Only the group opened last is asked unconditionally. The rest have to be within reach
    /// vertically first, which changes no verdict — the reach test is the same vertical gap the
    /// judgement applies — and keeps the trace from filling with every pair of lines on the page.
    /// </para>
    /// </remarks>
    private static List<OcrTextBlock>? GroupThisLineContinues(
        List<List<OcrTextBlock>> groups,
        OcrTextBlock current,
        IReadOnlyList<OcrTextBlock> lines,
        GroupingProfile profile,
        List<NextLineDecision>? decisions)
    {
        List<OcrTextBlock>? best = null;
        var bestAlignment = double.PositiveInfinity;
        var bestDistance = double.PositiveInfinity;

        // Back to front, so the group opened last is the first one seen and keeps a tie by having
        // been chosen already.
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var group = groups[i];
            var previous = group[^1];

            var isNearestGroup = i == groups.Count - 1;
            if (!isNearestGroup && !IsWithinContinuationReach(previous, current))
                continue;

            if (!NothingLiesBetween(previous, current, lines))
                continue;

            if (!CanJoinNextLine(group, current, profile, decisions))
                continue;

            var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
            var alignment = AlignmentDelta(previous, current) / Math.Max(1, avgHeight);
            var distance = (current.LayoutBounds.Y - previous.LayoutBounds.Y) / Math.Max(1, avgHeight);

            if (best is null ||
                alignment < bestAlignment - AlignmentTieBand ||
                (Math.Abs(alignment - bestAlignment) <= AlignmentTieBand && distance < bestDistance))
            {
                best = group;
                bestAlignment = alignment;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// How close two alignment figures have to be before they count as the same, in line heights.
    /// </summary>
    /// <remarks>
    /// A hundredth of a line height is well under a pixel at the sizes this runs at, so two groups
    /// separated by less than it are not really being told apart by alignment and the next test
    /// should decide instead. If the later tie-breaks turn out to be doing much of the work, that is
    /// a sign the alignment figure is too coarse to choose on, and worth reporting rather than
    /// accepting.
    /// </remarks>
    private const double AlignmentTieBand = 0.01;

    /// <summary>
    /// Whether a group is close enough vertically to be worth judging at all.
    /// </summary>
    /// <remarks>
    /// The same limits <see cref="JudgeNextLine"/> applies, so a group filtered out here would have
    /// been refused there for the same reason. Its only job is to stop the work — and the trace —
    /// growing with every pair of lines on a page rather than with the lines themselves.
    /// </remarks>
    private static bool IsWithinContinuationReach(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
        var verticalGap = current.LayoutBounds.Y - previous.LayoutBounds.Bottom;

        return verticalGap >= -avgHeight * 0.5 && verticalGap <= Math.Max(avgHeight * 0.8, 10);
    }

    /// <summary>
    /// Whether the space between two lines, in the column they share, is empty.
    /// </summary>
    /// <remarks>
    /// <para>Now that every open group is asked, two lines can be judged as neighbours with a third
    /// line sitting between them on the page. A news front page is the shape this is for: a
    /// headline, its standfirst, then the next headline, all in one column and all aligned. Without
    /// this the first and third read as a plausible continuation of each other and get strung
    /// together with the standfirst left out of the middle of them.</para>
    ///
    /// <para>Only the horizontal span the two lines actually share is examined, and a third line
    /// counts as being in the way when its own middle falls in the gap. A line beside the column, or
    /// one clipping into it by a few pixels of unclipped detection box, is not in the way of
    /// anything.</para>
    /// </remarks>
    private static bool NothingLiesBetween(
        OcrTextBlock previous, OcrTextBlock current, IReadOnlyList<OcrTextBlock> lines)
    {
        var left = Math.Max(previous.LayoutBounds.Left, current.LayoutBounds.Left);
        var right = Math.Min(previous.LayoutBounds.Right, current.LayoutBounds.Right);
        if (right <= left)
            return true;

        var top = previous.LayoutBounds.Bottom;
        var bottom = current.LayoutBounds.Top;
        if (bottom <= top)
            return true;

        foreach (var line in lines)
        {
            if (ReferenceEquals(line, previous) || ReferenceEquals(line, current))
                continue;

            var box = line.LayoutBounds;
            if (box.Right <= left || box.Left >= right)
                continue;

            var middle = box.Y + box.Height / 2.0;
            if (middle > top && middle < bottom)
                return false;
        }

        return true;
    }

    private static bool CanJoinNextLine(
        List<OcrTextBlock> group,
        OcrTextBlock current,
        GroupingProfile profile,
        List<NextLineDecision>? decisions)
    {
        var previous = group[^1];
        var (joined, rule) = JudgeNextLine(previous, current, profile);
        if (decisions is null)
            return joined;

        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
        decisions.Add(new NextLineDecision(
            "next",
            previous.Text,
            current.Text,
            previous.LayoutScript,
            current.LayoutScript,
            (current.LayoutBounds.Y - previous.LayoutBounds.Bottom) / Math.Max(1, avgHeight),
            Math.Abs(previous.LayoutBounds.X - current.LayoutBounds.X) / Math.Max(1, avgHeight),
            Math.Abs(CenterX(previous) - CenterX(current)) / Math.Max(1, avgHeight),
            Math.Abs(previous.LayoutBounds.Right - current.LayoutBounds.Right) / Math.Max(1, avgHeight),
            // From the same function the rule read, not recomputed here: a trace that works out the
            // verdict a second way is a trace that can disagree with the verdict.
            AlignmentDelta(previous, current) / Math.Max(1, avgHeight),
            TextSizeRatio(previous, current),
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
            LineAdvanceRatio(previous, current),
            WrappedFinalLineAdvance,
            SolidLineAdvance,
            joined,
            rule));

        return joined;
    }

    /// <summary>
    /// The leading: how far the second line sits below the first, in detection-box heights. One
    /// line advance is 1.0, so a paragraph reads a little under it and anything laid out on purpose
    /// reads well over.
    /// </summary>
    /// <remarks>
    /// Measured on <see cref="OcrTextBlock.LayoutBounds"/>, which is what makes a leading test
    /// possible at all. Against the normalised box the same typographic leading comes back as two
    /// different fractions — a Japanese Wikipedia paragraph read 0.37 between its lines where an
    /// English one read 0.09 — so the two populations overlapped and no threshold separated them.
    /// On the detector's own box they agree.
    /// </remarks>
    private static double LineAdvanceRatio(OcrTextBlock previous, OcrTextBlock current)
    {
        var box = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        return box > 0 ? (current.LayoutBounds.Y - previous.LayoutBounds.Y) / box : -1;
    }

    /// <summary>
    /// The horizontal centre of a line's detection box.
    /// </summary>
    private static double CenterX(OcrTextBlock line) =>
        line.LayoutBounds.X + line.LayoutBounds.Width / 2.0;

    /// <summary>
    /// How far out of line two lines are, in pixels: the closest of their left edges, their right
    /// edges and their centres.
    /// </summary>
    /// <remarks>
    /// <para>This used to read the left edges and nothing else, which is a test for text set flush
    /// left and a coin toss for anything else. Centred speech moves its left edge by half the
    /// difference in line length, so across the ten comic pages twenty-one pairs were refused as
    /// misaligned while their centres sat within a twentieth of a line of each other — including
    /// every bubble that opens on a short line, which is most of them.</para>
    ///
    /// <para>The right edge earns its place separately, and it is not symmetry for its own sake: a
    /// stat panel's body text is set flush right, so its consecutive lines read 6.35 and 7.60 line
    /// heights apart on the left and 0.00 on the right. Those pairs are ones the hand-marked
    /// grouping says belong together. What must stay apart there — a short label above that body —
    /// is far out on all three edges (3.18 / 6.33 / 9.49), so taking the smallest does not put it
    /// at risk.</para>
    ///
    /// <para>The thresholds this feeds do not move. The measurement was wrong for anything not set
    /// flush left; the limits on it were never the problem.</para>
    /// </remarks>
    private static double AlignmentDelta(OcrTextBlock previous, OcrTextBlock current) =>
        Math.Min(
            Math.Abs(previous.LayoutBounds.X - current.LayoutBounds.X),
            Math.Min(
                Math.Abs(previous.LayoutBounds.Right - current.LayoutBounds.Right),
                Math.Abs(CenterX(previous) - CenterX(current))));

    private static (bool Joined, string Rule) JudgeNextLine(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
        if (TextSizeRatio(previous, current) < MinTextSizeRatio)
            return (false, "text size");

        // The gap can be negative, for exactly the reason it can horizontally in JudgeSameLine:
        // the detector's unclip expansion grows every box a little past its glyphs, so two lines of
        // one wrapped sentence routinely come back overlapping by a few pixels. A `< 0` guard threw
        // those apart, and each half was then translated on its own — which is not a cosmetic
        // problem, because half a sentence is not half a translation. A game panel reading "Shots
        // deal more damage for each bullet remaining in the" / "magazine" overlapped by 3px, and
        // came back as 「每發子彈在」 and 「雜誌」; the whole sentence gives 「彈匣內每發子彈的傷害會增加」.
        //
        // Half a line of overlap, from measuring 54 candidate pairs across the logged captures. The
        // two populations do not touch: wrapped continuations land at -0.4 to -0.1 of a line, while
        // boxes that share a row (an "E" beside "PICK UP", an "X" beside "HOLD TO SALVAGE") land at
        // -0.9 to -1.0. Nothing at all falls between, so the threshold sits in empty space rather
        // than on either edge. Those row-mates are what this really has to keep out; the same-line
        // merge takes most of them first, and this is the backstop for the rest.
        var verticalGap = current.LayoutBounds.Y - (previous.LayoutBounds.Y + previous.LayoutBounds.Height);
        if (verticalGap < -avgHeight * 0.5 || verticalGap > Math.Max(avgHeight * 0.8, 10))
            return (false, "vertical gap");

        var alignmentDelta = AlignmentDelta(previous, current);
        if (alignmentDelta > Math.Max(avgHeight * 1.2, 18))
            return (false, "alignment");

        var overlap = Math.Max(
            0,
            Math.Min(previous.LayoutBounds.Right, current.LayoutBounds.Right) -
            Math.Max(previous.LayoutBounds.Left, current.LayoutBounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.LayoutBounds.Width, current.LayoutBounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || alignmentDelta <= Math.Max(avgHeight * 0.7, 12);
        if (!isAlignedContinuation)
            return (false, "not aligned enough to continue");

        return SentenceContinuationEvidence(previous, current, profile);
    }

    /// <summary>
    /// How close two lines are in text size, for deciding whether the second can be the first
    /// wrapping. Glyph heights when both sides carry one, the detection boxes otherwise.
    /// </summary>
    /// <remarks>
    /// <para>Comparing boxes was measuring the wrong thing. A Latin box runs about half again as
    /// tall as the letters inside it, and how much of that slack there is depends on which letters
    /// the word happens to contain — "magazine" has no ascender, so its box came back 26px under a
    /// 30px line it belonged to, a ratio of 0.867 that this gate refused by 0.013. The same
    /// sentence, re-read as the picture moved, gave anything from 0.68 to 1.00 across eight passes:
    /// text that merged or did not depending on the frame.</para>
    ///
    /// <para>This is the argument <see cref="SharesVisualRow"/> already makes about its own height
    /// test — box height is a property of the letters, not of whether they belong together — and the
    /// answer there was to keep the test tolerant. Here there is a better one available: the engine
    /// already measures the ink for Latin lines, because both overlays need it to size their font.
    /// On that number the same pair reads 0.937, and it is steady.</para>
    ///
    /// <para>Measured over the corpus, switching the comparison admits three pairs — the sentence
    /// above and one more wrapped English line — and separates none that were already joined. The
    /// threshold does not move; it was never the problem. See issue #79.</para>
    ///
    /// <para>Glyph heights are compared only within one script. A Latin box carries ascender and
    /// descender room a CJK one does not, so the two numbers do not mean the same thing, and no
    /// constant converts between them — that was measured, and the ratio turned out to be a
    /// property of the particular words rather than of the scripts.</para>
    ///
    /// <para>Everything else falls back to the raw detection boxes, which is coarse but at least
    /// impartial: one detector, one procedure, whatever the text is. It is what the old code did
    /// too, except that it compared the *normalised* boxes — and those are 0.820 of each other
    /// across scripts before a single letter is read, so every Latin line beside a CJK one was
    /// refused by a size test it could not have passed. That is issue #164's 「OPTIONS／ゲーム設定」.</para>
    /// </remarks>
    internal static double TextSizeRatio(OcrTextBlock previous, OcrTextBlock current)
    {
        if (previous.LayoutScript == current.LayoutScript &&
            previous.LayoutGlyphHeight is { } previousGlyph and > 0 &&
            current.LayoutGlyphHeight is { } currentGlyph and > 0)
            return Math.Min(previousGlyph, currentGlyph) / Math.Max(previousGlyph, currentGlyph);

        return Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) /
               Math.Max(previous.LayoutBounds.Height, current.LayoutBounds.Height);
    }

    private static (bool Joined, string Rule) SentenceContinuationEvidence(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile)
    {
        var previousText = previous.Text.Trim();
        var currentText = current.Text.Trim();
        if (previousText.Length == 0 || currentText.Length == 0)
            return (false, "empty text");

        // The two refusals below are asked before any evidence for joining, because both of the
        // shapes they describe also look like evidence. Asked afterwards they would never be
        // reached: a colon is already read as a clause carrying on, and a bulleted line under a
        // longer one is already the shape of a paragraph's last line.
        if (StartsWithListBullet(currentText))
            return (false, "list bullet");

        if (EndsWithLabelColon(previousText) &&
            LineAdvanceRatio(previous, current) > SolidLineAdvance)
            return (false, "label colon");

        if (HasUnclosedDelimiter(previousText) ||
            EndsWithContinuationPunctuation(previousText) ||
            StartsWithContinuationPunctuation(currentText))
            return (true, "punctuation");

        if (EndsWithSentenceTerminator(previousText))
            return (false, "sentence terminator");

        // Lines set solid under one another: same leading, same edge, no width difference to read.
        // Asked before the shape test below because the shape test cannot see them — a paragraph's
        // middle lines are all about as wide as each other, which is the one thing that rule takes
        // as proof that nothing wrapped.
        if (IsSetSolidUnder(previous, current, profile))
            return (true, "set solid");

        // A much shorter following line is a common natural wrap shape.
        // Similar-width lines without linguistic evidence are kept separate
        // so title/body pairs are not merged just because they align.
        //
        // Guarded by the length test below, because on its own that shape is also what a stack of
        // menu entries looks like — a longer label above a shorter one, aligned, evenly spaced. It
        // read 「アビリティ」over「召喚石」and 「캐릭터강화」over「소지품」as wrapped sentences, glued
        // each pair into one string for the translator, and squeezed both into one bubble. See #75.
        if (!IsLongEnoughToHaveWrapped(previous) ||
            previous.LayoutBounds.Width < current.LayoutBounds.Width * 1.35)
            return (false, "no continuation evidence");

        // A paragraph's last line is set at the paragraph's leading, so a pair spaced further apart
        // than that is not a paragraph ending — it is a heading over the thing it labels, or the
        // next entry in a list. This rule had no leading test at all, which is how a settings panel
        // joined "Reveal all rooms before proceeding to next floor" to the unrelated checkbox under
        // it purely because that one was shorter.
        return LineAdvanceRatio(previous, current) <= WrappedFinalLineAdvance
            ? (true, "shorter final line")
            : (false, "leading");
    }

    /// <summary>
    /// Whether a line is long enough that running out of room — and so wrapping — is plausible.
    /// </summary>
    /// <remarks>
    /// <para>Measured in line heights rather than characters so that one number serves every script:
    /// a CJK line runs about one character per line height, a Latin one about two. So this asks for
    /// roughly eight CJK characters, or sixteen Latin ones.</para>
    ///
    /// <para>Across the 329-image corpus the two populations are far apart: every genuinely wrapped
    /// sentence reached 16.6 and every stacked label stopped at 6.8, with nothing in between. The
    /// threshold sits at the low end of that empty band on purpose. Letting a label through costs
    /// two crowded words in one bubble; keeping a real wrapped sentence apart costs the translator
    /// half a sentence, which is the far worse failure — it is the whole of #74.</para>
    /// </remarks>
    private const double WrappedLineMinAspect = 8.0;

    /// <summary>
    /// The most leading a shorter following line can have and still be the end of the paragraph
    /// above it.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the image corpus on LayoutBounds, after the row rule had stopped side by
    /// side things being joined: every pair that is really one sentence wrapping sits at or under
    /// 1.33 — a Korean panel's instruction at 1.26 and 1.33, an English page's "Resources for
    /// Developers," / "by Developers" at 1.32 — while the pairs that must come apart start at 1.40:
    /// three checkbox entries of the settings panel at 1.40 and 1.43, and a documentation site's
    /// breadcrumb over its filter box at 1.55. Nothing falls between 1.33 and 1.40.</para>
    ///
    /// <para>1.38 is where the branch this was ported from put it, on a different corpus and a
    /// reconstructed detection box; landing inside the empty band measured here as well is the
    /// reason to keep the number rather than move it to the midpoint.</para>
    ///
    /// <para>Every script, not Latin only. The port gated this on Latin because it had to rebuild
    /// the detector's box from the normalised one and that reconstruction was only approximate for
    /// CJK. LayoutBounds is the box, so there is nothing to approximate and nothing to gate.</para>
    /// </remarks>
    private const double WrappedFinalLineAdvance = 1.38;

    /// <summary>
    /// Whether the second line is set solid under the first: one leading, one edge, no gap in the
    /// setting that a reader would take as a break.
    /// </summary>
    /// <remarks>
    /// <para>The evidence rule below it only recognises a paragraph's LAST line, because the shape
    /// it looks for is a much shorter line under a longer one. Every line in the middle of a
    /// paragraph is about as wide as the one above it, so a three-line subtitle came back as three
    /// separate requests to the translator — thirteen refusals across the ten comic pages, all
    /// reading "no continuation evidence" over pairs whose width ratio was 0.92 to 1.22.</para>
    ///
    /// <para>What replaces the width reading is the setting itself. Two conditions, both strict:
    /// the leading is no looser than text set solid, and the two lines share an edge to within a
    /// third of a line height. The alignment figure here is far tighter than the ordinary gate
    /// (0.35 against 1.2), and it is what keeps a stat panel's label off its body — those pairs sit
    /// at 2.90, 5.07 and 7.42 — so it is a threshold to leave alone rather than one to tune.</para>
    ///
    /// <para>Size is not re-tested here. The caller has already held the pair to the ordinary size
    /// ratio, and measuring it a second time on the earlier branch pushed back out the very
    /// sentences this exists to join.</para>
    ///
    /// <para>The length test is the caller's own: a line too short to have run out of room is not
    /// wrapping, whatever its spacing. A speech bubble opening on one or two words is exactly that
    /// shape and exactly not that case, which is what <see cref="GroupingProfile"/> waives — for
    /// the mode where the user has said the capture is speech, and only there.</para>
    /// </remarks>
    private static bool IsSetSolidUnder(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        return LineAdvanceRatio(previous, current) <= SolidLineAdvance &&
               AlignmentDelta(previous, current) <= Math.Max(avgHeight * SetSolidMaxAlignment, 6) &&
               (IsLongEnoughToHaveWrapped(previous) || profile.WaiveLengthTestWhenSetSolid);
    }

    /// <summary>
    /// The most leading two lines can have and still read as set solid, one under the other.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the whole image corpus at both detector sizes, on the pairs that reach
    /// this test at all — through the size gate and the shared-edge gate, and long enough to have
    /// wrapped. The two populations are NOT separated by a gap, and that is the first thing to know
    /// about this number. Lines that must join run from 0.65 to 1.25; lines that must not run from
    /// 0.86 to 1.58 — a settings panel's checkboxes at 1.47 to 1.58, but also a news page's
    /// consecutive headlines at 1.12 and 1.25, an event listing's two dates at 1.17, and a game's
    /// character rows at 0.86 to 1.03. They overlap for the whole of that range. No value of this
    /// constant separates them, so it is not chosen to.</para>
    ///
    /// <para>What it is chosen on is the cost of being wrong in each direction, which this codebase
    /// has already settled: joining two labels puts two extra words in one bubble, while splitting
    /// one sentence hands the translator half of it. So the number sits high in the overlap rather
    /// than below it — 36 of the corpus's 37 correct joins, against 6 wrong ones. Tightening to
    /// 1.05, just above the comic pages' loosest real wrap, removes 3 of those 6 and costs 7 of the
    /// correct ones, among them three Japanese Wikipedia paragraphs and a game's tutorial text. The
    /// three it cannot remove at any setting are a game's character rows, which sit at 0.86 to 1.03,
    /// below every candidate.</para>
    ///
    /// <para>It is one number for every layout, and an adaptive version was tried: let a group that
    /// has already been set at some leading be judged against its own. It was measured over the
    /// whole corpus and changed exactly one verdict, which was a wrong one — two unrelated news
    /// headlines strung together — so it was taken out again. The reason it bought nothing is worth
    /// keeping: a group's own leading only exists once it has two lines, so the earliest it can
    /// apply is a third line, while nearly every pair that needs the limit relaxed is a second one.
    /// A list of stacked rows cannot exploit such a rule either, for the same reason — its entries
    /// never join, so its groups never reach a second line to establish anything with.</para>
    ///
    /// <para>The ceiling is not from the corpus. A fixture written before any of this — a Chinese
    /// heading over its byline, one line apart, similar widths — is set at 1.25, and it is there to
    /// say that pair must not join. Three corpus cases agree with it: a news page's consecutive
    /// headlines at 1.12 and 1.25, and an event listing's two dates at 1.17. So the limit stays
    /// under that fixture rather than over it, which costs one Japanese Wikipedia paragraph at
    /// 1.24 and keeps a guard that was written deliberately.</para>
    ///
    /// <para>Both detector sizes agree on the figures that matter here: the comic pages read
    /// identically at 2048 and 1600 (they are small enough not to be resized either way), and the
    /// settings panel's list sits at 1.47 and above at both. The one pair that moves is the event
    /// listing, 1.17 native and 0.95 downscaled, which is inside the joining population at the
    /// downscaled end — one more reason no gap exists to aim at.</para>
    /// </remarks>
    private const double SolidLineAdvance = 1.20;

    /// <summary>
    /// How far apart two set-solid lines' nearest edges may be, in line heights.
    /// </summary>
    /// <remarks>
    /// Much tighter than the ordinary alignment gate's 1.2, and deliberately so: this is the one
    /// test standing between a stat panel's label and the body under it once the width reading is
    /// no longer being consulted. Measured on the comic corpus, the pairs that must stay apart sit
    /// at 2.90, 5.07 and 7.42 line heights, and the pairs that must join sit at 0.01 to 0.10. The
    /// band between those is enormous; this number is in it, not on either edge.
    /// </remarks>
    private const double SetSolidMaxAlignment = 0.35;

    private static bool IsLongEnoughToHaveWrapped(OcrTextBlock line) =>
        line.LayoutBounds.Height > 0 &&
        line.LayoutBounds.Width / line.LayoutBounds.Height >= WrappedLineMinAspect;

    private static bool HasUnclosedDelimiter(string text) =>
        Count(text, '「') > Count(text, '」') ||
        Count(text, '『') > Count(text, '』') ||
        Count(text, '（') > Count(text, '）') ||
        Count(text, '(') > Count(text, ')') ||
        Count(text, '【') > Count(text, '】') ||
        Count(text, '《') > Count(text, '》') ||
        Count(text, '〈') > Count(text, '〉') ||
        Count(text, '"') % 2 == 1;

    private static int Count(string text, char value) => text.Count(c => c == value);

    private static bool EndsWithContinuationPunctuation(string text) =>
        text[^1] is '、' or '，' or ',' or '：' or ':' or '；' or ';' or '「' or '『' or '（' or '(' or '【' or '《' or '〈';

    private static bool StartsWithContinuationPunctuation(string text) =>
        text[0] is '」' or '』' or '）' or ')' or '】' or '》' or '〉' or '、' or '，' or ',' or '。' or '！' or '？' or '!' or '?';

    private static bool EndsWithSentenceTerminator(string text) =>
        text[^1] is '。' or '！' or '？' or '!' or '?' or '.';

    /// <summary>
    /// Whether a line opens with the mark that makes it an item in a list.
    /// </summary>
    /// <remarks>
    /// News portals set their headline lists at the same leading as running prose — 1.22 and 1.23
    /// line heights on the pages measured, against a paragraph's 1.30 to 1.40 — so no spacing rule
    /// can tell one from the other. The mark can: nothing writes a bullet in the middle of a
    /// sentence it is continuing. This refuses whatever the geometry says, which is the point of
    /// it — the geometry has already been asked and had nothing to offer.
    /// </remarks>
    private static bool StartsWithListBullet(string text) =>
        text[0] is '·' or '・' or '•' or '‧' or '●' or '○' or '▪' or '◆' or '※';

    /// <summary>
    /// Whether a line ends on a colon, which reads two ways and cannot be told apart by the
    /// punctuation alone.
    /// </summary>
    /// <remarks>
    /// <para>A colon at the end of a line is either a clause that has not finished — "as follows:"
    /// — or a form label standing over its value. Both are common, and the continuation rule reads
    /// every one of them as the first. A settings dialog gave "Column type:" over "Standard" a line
    /// and three quarters below, which is a label and its value on two rows of a form, and it was
    /// joined into one string for the translator.</para>
    ///
    /// <para>So the colon stops being evidence on its own and becomes evidence only when the two
    /// lines are set as close as a wrapped sentence is. The limit is the same one a paragraph's
    /// last line is held to today; the step that measures a leading of its own will move this onto
    /// it.</para>
    /// </remarks>
    private static bool EndsWithLabelColon(string text) => text[^1] is ':' or '：';

    private static OcrTextBlock BuildGroup(List<OcrTextBlock> blocks)
    {
        if (blocks.Count == 1)
            return blocks[0];

        var x = blocks.Min(block => block.Bounds.X);
        var y = blocks.Min(block => block.Bounds.Y);
        var right = blocks.Max(block => block.Bounds.Right);
        var bottom = blocks.Max(block => block.Bounds.Bottom);
        var text = string.Join(" ", blocks.Select(block => block.Text.Trim()).Where(text => text.Length > 0));

        // Aggregate the per-line glyph heights (Latin only; null for CJK) so the group's overlay
        // font is sized from the real glyph height, while Bounds remains the full coverage area.
        var glyphHeights = blocks
            .Where(block => block.RenderGlyphHeight.HasValue)
            .Select(block => block.RenderGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();
        double? groupGlyphHeight = glyphHeights.Count > 0 ? glyphHeights[glyphHeights.Count / 2] : null;

        var layoutScript = LayoutScriptDetection.For(text);

        return new OcrTextBlock(
            text,
            new Rect(x, y, right - x, bottom - y),
            blocks.Select(block => block.Bounds).ToList(),
            groupGlyphHeight,
            CombineConfidence(blocks),
            // Re-read rather than folded together: the field's contract is "the script of this
            // block's own text", and a group whose lines are Latin and CJK is exactly the Mixed
            // that the joined text reports.
            layoutScript,
            blocks.Select(block => block.LayoutBounds).Aggregate(Rect.Union),
            CombineLayoutGlyphHeight(layoutScript, blocks));
    }

    /// <summary>
    /// One confidence for several fragments, weighted by how much text each contributes.
    /// </summary>
    /// <remarks>
    /// A plain average lets a two-character fragment beside a confidently-read line pull the whole
    /// group down as far as the line itself pulls it up, which would make the group's score depend
    /// on how the detector happened to split the line rather than on how well it was read.
    /// </remarks>
    private static double? CombineConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        double weighted = 0;
        double weight = 0;

        foreach (var block in blocks)
        {
            if (block.Confidence is not { } confidence) continue;

            var characters = Math.Max(1, block.Text.Trim().Length);
            weighted += confidence * characters;
            weight += characters;
        }

        return weight > 0 ? weighted / weight : null;
    }

    /// <summary>
    /// One layout glyph height for several lines: the median of theirs, and nothing at all once
    /// the joined text is no longer of a single script.
    /// </summary>
    private static double? CombineLayoutGlyphHeight(OcrLayoutScript script, IReadOnlyList<OcrTextBlock> blocks)
    {
        if (script is not (OcrLayoutScript.Latin or OcrLayoutScript.Cjk))
            return null;

        var heights = blocks
            .Where(block => block.LayoutGlyphHeight is > 0)
            .Select(block => block.LayoutGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();

        return heights.Count > 0 ? heights[heights.Count / 2] : null;
    }

    private static double? CombineGlyphHeight(double? a, double? b) =>
        (a, b) switch
        {
            (double ha, double hb) => (ha + hb) / 2.0,
            (double ha, null) => ha,
            (null, double hb) => hb,
            _ => null,
        };
}
