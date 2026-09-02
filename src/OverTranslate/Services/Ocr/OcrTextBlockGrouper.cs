using System.Windows;

namespace OverTranslate.Services.Ocr;

internal static class OcrTextBlockGrouper
{
    public static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks) =>
        Group(blocks, null, null);

    /// <param name="trace">
    /// Collects every verdict and the gap measurement behind them. Null in the app; OcrHarness
    /// passes one, because these thresholds cannot be tuned from the grouped output alone — it
    /// shows what was joined, never how close the rest came to being.
    /// </param>
    /// <param name="appearance">
    /// What the lines look like on the capture, or null to decide on geometry alone. Optional
    /// because the answer has to be the same shape without it: a caller with no picture to hand,
    /// and a capture whose pixels could not be read, both get the geometry-only result rather than
    /// an error. See <see cref="VisualSplitEvidence"/> for what it is allowed to decide.
    /// </param>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks, GroupTrace? trace, IBlockAppearanceSource? appearance)
    {
        if (blocks.Count <= 1)
        {
            // Nothing to measure and nothing to merge, but the trace still has to say so — an
            // unset threshold reads as 0.00, which is a number this never chose.
            if (trace is not null) trace.SameLineThreshold = SameLineGapThreshold.Estimate([]);
            return blocks.ToList();
        }

        var sameLineMerged = MergeSameLineFragments(blocks, trace);

        // Asked of the whole capture before any pair is judged, for the reason MergeSameLineFragments
        // gives about its own measuring pass: what a row is depends on the rows around it, and a
        // question about the layout cannot be answered from the two lines in front of it.
        var repeatedRows = RepeatedRowLayout.Detect(sameLineMerged);

        var sorted = sameLineMerged
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();

        var groups = new List<List<OcrTextBlock>>();
        foreach (var block in sorted)
        {
            var continued = GroupThisContinues(
                groups, block, sorted, appearance, repeatedRows, trace);
            if (continued is not null) continued.Add(block);
            else groups.Add([block]);
        }

        return groups.Select(BuildGroup).ToList();
    }

    /// <summary>
    /// What the grouper did and what it measured to decide, for the offline harness.
    /// </summary>
    internal sealed class GroupTrace
    {
        public List<GroupDecision> Decisions { get; } = [];

        /// <summary>Every neighbour-to-neighbour space on every row, in line heights.</summary>
        public IReadOnlyList<double> SameLineGaps { get; internal set; } = [];

        /// <summary>The one drawn from those gaps, or the fallback and why.</summary>
        public SameLineGapThreshold SameLineThreshold { get; internal set; }
    }

    /// <summary>
    /// One merge verdict, with its geometry in line heights so numbers from captures of different
    /// sizes can be read side by side.
    /// </summary>
    /// <param name="Kind">
    /// "row" for the same-line test, which decides whether two boxes are one line of text, and
    /// "line" for the next-line test, which decides whether one line continues another.
    /// </param>
    /// <param name="Gap">Horizontal for a row, vertical for a line.</param>
    /// <param name="Fit">Vertical overlap for a row, alignment delta for a line.</param>
    /// <param name="Rule">Which test decided, whether it joined or refused.</param>
    /// <param name="BackgroundDistance">
    /// How far apart the two lines' surfaces look, in CIELAB, or -1 when no capture was available
    /// to sample. Traced for every next-line verdict and not only the ones colour decided, because
    /// what has to be checked is that the pairs it does not refuse are the ones that look alike.
    /// </param>
    /// <param name="ForegroundDistance"><inheritdoc cref="BackgroundDistance"/></param>
    /// <param name="LeadingBar">
    /// The most leading this pair was allowed, which is <see cref="SolidLineAdvance"/> unless the
    /// group above had already established a tighter one of its own.
    /// </param>
    internal readonly record struct GroupDecision(
        string Kind,
        string Previous,
        string Current,
        double Gap,
        double Fit,
        double TextSizeRatio,
        double WidthRatio,
        bool Joined,
        string Rule,
        double BackgroundDistance = -1,
        double ForegroundDistance = -1,
        double Leading = -1,
        double LeadingBar = -1);

    /// <summary>
    /// Rebuilds the lines the detector split, then decides which of them are one line of text.
    /// </summary>
    /// <remarks>
    /// <para>Measure first, decide second, merge last — three passes rather than one. Merging as it
    /// went meant every gap after the first was measured against a box that had already grown: by
    /// the fourth navigation entry the comparison was against the whole left half of the bar, whose
    /// height was the tallest of everything absorbed so far. The distances that decide this are
    /// between neighbours, so they are all taken from the boxes the detector actually returned,
    /// before anything is joined.</para>
    ///
    /// <para>Rows are built without consulting the gaps at all, because sharing a row and being one
    /// line are different questions and answering them together is what let a spacing rule decide
    /// which row a box belonged to. A row is only "these boxes sit on one line of the picture"; what
    /// the spaces along it mean is the next question, and it is asked of the whole capture at once
    /// so that the answer can come from this capture's own spacing.</para>
    /// </remarks>
    private static List<OcrTextBlock> MergeSameLineFragments(
        IReadOnlyList<OcrTextBlock> blocks, GroupTrace? trace)
    {
        var rows = BuildVisualRows(blocks);
        var gaps = rows.SelectMany(AdjacentGaps).ToList();
        var threshold = SameLineGapThreshold.Estimate(gaps);

        if (trace is not null)
        {
            trace.SameLineGaps = gaps;
            trace.SameLineThreshold = threshold;
        }

        return rows.SelectMany(row => SplitRowIntoLines(row, threshold.Value, trace)).ToList();
    }

    /// <summary>
    /// Gathers the boxes that sit on one line of the picture, left to right, whatever the spaces
    /// between them turn out to mean.
    /// </summary>
    /// <remarks>
    /// Sorted by X and not by Y. Latin word boxes on one line have tops that vary with ascenders
    /// and descenders (a real line gave "Send" at y=32 beside "to" at y=37), so a Y-primary sort
    /// interleaves words from across the capture and a row built in that order ends up holding
    /// fragments of several. Reading order keeps a line's own boxes together, and the vertical
    /// overlap test routes each box to the right row when several are open at once.
    /// </remarks>
    private static List<List<OcrTextBlock>> BuildVisualRows(IReadOnlyList<OcrTextBlock> blocks)
    {
        var ordered = blocks
            .OrderBy(block => block.Bounds.X)
            .ThenBy(block => block.Bounds.Y)
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
        // neighbour. A short mid-line word ("to" h=25 on a h=31 line → heightRatio 0.81) sits on the
        // same baseline, so its box is fully nested in the line (overlap ≈ 1.0). Two stacked buttons
        // ("Download…report" h=32 next to a vertically offset "Create key" h=38 → heightRatio 0.84)
        // overlap only ≈ 0.47. Their height ratios are inverted (0.81 < 0.84), so any single height
        // threshold either drops "to" or merges the buttons. Keep height tolerant and let the strict
        // overlap test below do the discriminating.
        // 0.5 rather than 0.6, measured: a subtitle reading "Let's pay CiRCLE a visit on the way
        // home." came back as an 888x88 box and a 141x51 one holding "home", a height ratio of
        // 0.58. They sit 2px apart with the shorter box entirely inside the taller one's rows, and
        // the guard rejected them anyway — so "home" was translated on its own and appeared as a
        // stray word to the right of a sentence missing its ending. The word's box is short because
        // the word has no descender, which is a property of the letters, not of whether they belong
        // to the line. Discriminating between lines is the vertical-overlap test's job, as the note
        // above says; this only has to keep out boxes of wildly different size.
        var heightRatio = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                          Math.Max(previous.Bounds.Height, current.Bounds.Height);

        return heightRatio >= MinimumRowHeightRatio &&
               VerticalOverlapRate(previous, current) >= MinimumRowVerticalOverlap;
    }

    private static double VerticalOverlapRate(OcrTextBlock previous, OcrTextBlock current)
    {
        var overlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Bottom, current.Bounds.Bottom) -
            Math.Max(previous.Bounds.Top, current.Bounds.Top));

        return overlap / Math.Max(1, Math.Min(previous.Bounds.Height, current.Bounds.Height));
    }

    /// <summary>The space before each box in a row, in line heights, neighbour to neighbour.</summary>
    private static IEnumerable<double> AdjacentGaps(List<OcrTextBlock> row) =>
        row.Zip(row.Skip(1), (left, right) => NormalizedGap(left, right));

    private static double NormalizedGap(OcrTextBlock previous, OcrTextBlock current) =>
        (current.Bounds.X - previous.Bounds.Right) /
        Math.Max(1, (previous.Bounds.Height + current.Bounds.Height) / 2.0);

    /// <summary>
    /// Cuts one row wherever the space between neighbours is too wide to be a space between words,
    /// and joins what is left.
    /// </summary>
    private static IEnumerable<OcrTextBlock> SplitRowIntoLines(
        List<OcrTextBlock> row, double threshold, GroupTrace? trace)
    {
        var lines = new List<OcrTextBlock>();
        var line = row[0];

        for (var i = 1; i < row.Count; i++)
        {
            var (joined, rule) = JudgeSameLine(row[i - 1], row[i], threshold);
            trace?.Decisions.Add(SameLineDecision(row[i - 1], row[i], joined, rule));

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

    /// <summary>
    /// Whether two neighbours on one row are one line of text, judged on the boxes as detected.
    /// </summary>
    /// <param name="threshold">
    /// The widest space that still reads as a space between words, in line heights — this capture's
    /// own if it could be measured, <see cref="SameLineGapThreshold.Fallback"/> otherwise.
    /// </param>
    private static (bool Joined, string Rule) JudgeSameLine(
        OcrTextBlock previous, OcrTextBlock current, double threshold)
    {
        if (!SharesVisualRow(previous, current))
            return (false, "not one row");

        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;

        // The gap can be negative: on large captures the detector's unclip expansion enlarges big
        // heading word-boxes until adjacent ones overlap horizontally (e.g. "Translate" right=533
        // vs "your website" left=515 → gap -18), which a `>= 0` guard rejected, scattering one
        // heading into word-by-word translations. Allow up to a line-height of overlap; the
        // vertical-overlap and height-ratio checks above already keep stacked/unrelated lines out.
        var horizontalGap = current.Bounds.X - previous.Bounds.Right;
        if (horizontalGap < -avgHeight)
            return (false, "overlaps too far");

        // The floor is for text too small for a ratio to mean much. It was 18px, which at the sizes
        // it applied to was itself wide enough to cross a menu.
        return horizontalGap <= Math.Max(avgHeight * threshold, MinimumRowGapPixels)
            ? (true, "same row")
            : (false, "horizontal gap");
    }

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowHeightRatio = 0.5;

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowVerticalOverlap = 0.72;

    /// <inheritdoc cref="JudgeSameLine"/>
    private const double MinimumRowGapPixels = 6;

    private static GroupDecision SameLineDecision(
        OcrTextBlock previous, OcrTextBlock current, bool joined, string rule) =>
        new("row",
            previous.Text,
            current.Text,
            NormalizedGap(previous, current),
            VerticalOverlapRate(previous, current),
            Math.Min(previous.Bounds.Height, current.Bounds.Height) /
            Math.Max(1, Math.Max(previous.Bounds.Height, current.Bounds.Height)),
            previous.Bounds.Width / Math.Max(1, current.Bounds.Width),
            joined,
            rule);

    private static OcrTextBlock MergeSameLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var left = Math.Min(previous.Bounds.Left, current.Bounds.Left);
        var top = Math.Min(previous.Bounds.Top, current.Bounds.Top);
        var right = Math.Max(previous.Bounds.Right, current.Bounds.Right);
        var bottom = Math.Max(previous.Bounds.Bottom, current.Bounds.Bottom);
        var text = JoinInlineText(previous.Text, current.Text);

        return new OcrTextBlock(
            text,
            new Rect(left, top, right - left, bottom - top),
            SourceGlyphHeight: CombineGlyphHeight(previous.SourceGlyphHeight, current.SourceGlyphHeight),
            Confidence: CombineConfidence([previous, current]));
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
    /// Which of the groups opened so far <paramref name="block"/> is the next line of, or null when
    /// it starts one of its own.
    /// </summary>
    /// <remarks>
    /// <para>Every open group is a candidate, not only the one opened last. Blocks arrive sorted top
    /// to bottom, which on a single column puts a line's continuation immediately after it — but on
    /// a page of columns the columns interleave, and the line before a continuation in that order
    /// belongs to the column beside it. Two cards side by side arrive as left first line, right
    /// first line, left second line, right second line, so a rule that only looks back one place
    /// never once compares a line with the line it wrapped from. The reported symptom is exactly
    /// that shape: a card whose description merges when it is alone on screen stops merging when
    /// the page holds several, and which ones survive looks arbitrary because it depends on how the
    /// columns happen to line up vertically.</para>
    ///
    /// <para>This is the same answer <see cref="BuildVisualRows"/> already gives horizontally, where
    /// a box is routed to the best of the open rows rather than to the most recent one. The reason
    /// is the same too: reading order interleaves whenever the layout has more than one column in
    /// it, so "the previous one" is not the same thing as "the one this belongs to".</para>
    ///
    /// <para>Where several groups would take the line, the best-aligned wins. Columns close enough
    /// together for more than one to qualify are exactly the case where the nearest edge is the
    /// evidence, and taking the most recent instead would hand the line to whichever column happened
    /// to be further right.</para>
    ///
    /// <para>Measured over nine pages captured at 1600x1000 — MDN, Wikipedia, Hacker News, Yahoo!
    /// JAPAN and Naver, in English, Japanese and Korean — this makes 49 joins that were not being
    /// made at all. On the card grids and article pages every one of them is a wrapped sentence:
    /// MDN's home page went from 2 joins to 9, English Wikipedia from 0 to 16, Japanese Wikipedia
    /// from 4 to 12. Eleven of the 49 are stacked labels rather than sentences, nine of them one
    /// site's sidebar menu, and they are the known cost: a menu entry and a line of a subtitle are
    /// the same two boxes at the same spacing, and no geometry here separates them. The leading does
    /// not — that capture's own wrapped lines run 0.10 to 0.20 of a line and its menu 0.26 to 0.41,
    /// but another capture's genuine paragraphs run 0.32 to 0.43, and within a capture the two
    /// populations are a continuum with no gap to cut at. This is the trade the rest of this file
    /// already states: a label pair costs two words crowded into one bubble, while a sentence left
    /// in halves costs the translator the sentence.</para>
    ///
    /// <para>A grid of labels is where that trade is least favourable, and a product architecture
    /// diagram measured 3 joins right against 3 wrong: the wrapped captions in the top row joined,
    /// and so did two pairs of list items and one heading whose ink the capture could not tell from
    /// its content's. The page is a grid, so before this it made no joins at all, right or wrong. A
    /// fourth wrong one — a blue heading on grey over black content on white — is refused by
    /// <see cref="VisualSplitEvidence"/> rather than by anything here. Two more captions that should
    /// have joined were refused on text size at 0.87 and 0.88 against a bar of 0.88, which is the
    /// glyph-height noise <see cref="TextSizeRatio"/> describes and is not this scan's doing.</para>
    /// </remarks>
    private static List<OcrTextBlock>? GroupThisContinues(
        List<List<OcrTextBlock>> groups,
        OcrTextBlock block,
        IReadOnlyList<OcrTextBlock> lines,
        IBlockAppearanceSource? appearance,
        RepeatedRowLayout repeatedRows,
        GroupTrace? trace)
    {
        List<OcrTextBlock>? best = null;
        var bestAlignment = double.PositiveInfinity;
        var nearest = true;

        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var last = groups[i][^1];

            if (!NothingLiesBetween(lines, last, block)) continue;

            // The nearest group above is judged whatever its geometry, because its verdict and the
            // numbers behind it are what --group-explain reads: a pair refused at 0.83 of a line is
            // a different problem from one refused at 3.0, and only the near misses say which. The
            // ones further up are judged only when a continuation could reach them at all —
            // otherwise the trace would fill with the distance from every line to every other line
            // on the page, which is the noise that hides the near misses.
            if (!nearest && !IsWithinContinuationReach(last, block)) continue;
            nearest = false;

            if (!CanJoinNextLine(groups[i], block, appearance, repeatedRows, trace)) continue;

            var alignment = AlignmentDelta(last, block);
            if (alignment >= bestAlignment) continue;

            bestAlignment = alignment;
            best = groups[i];
        }

        return best;
    }

    /// <summary>
    /// Whether the space between two lines is empty, or whether a third line stands in it.
    /// </summary>
    /// <remarks>
    /// <para>A line continues the line directly above it and no other. Judging the most recent group
    /// alone used to enforce that by accident — the group before a block in reading order is the one
    /// directly above it, as long as the page has one column — and taking that accident away without
    /// replacing it was expensive. Measured on a Hacker News front page, 1600x1000: every entry has
    /// a byline set under it in smaller type, so entry titles are two rows apart, and the scan
    /// reached over each byline to hand title 2 to title 1's group. Twenty-two consecutive titles
    /// were chained into one "sentence" before this went in; with it, none of them are.</para>
    ///
    /// <para>Only the width the two lines share is examined. A line elsewhere on the page is at the
    /// same height as almost everything, and asking whether anything at all sits between two rows
    /// would refuse every capture with two columns in it — which is the shape this whole path exists
    /// to serve.</para>
    /// </remarks>
    private static bool NothingLiesBetween(
        IReadOnlyList<OcrTextBlock> lines, OcrTextBlock previous, OcrTextBlock current)
    {
        var top = previous.Bounds.Bottom;
        var bottom = current.Bounds.Top;

        // Two boxes that touch or overlap have no space between them for anything to stand in, and
        // the detector's unclip expansion makes overlap the ordinary case for two lines of one
        // wrapped sentence — every joined pair on the MDN capture measured between -0.49 and -0.04
        // of a line. Without this the span below is inverted, and a tall neighbour reaching past
        // both lines satisfies neither bound and reads as though it were between them. Measured on
        // a product architecture diagram, that refused every wrapped label on the page: 47 lines,
        // 47 groups, and "AI financial" / "report analysis" never even reached a verdict.
        if (bottom <= top) return true;

        var left = Math.Max(previous.Bounds.Left, current.Bounds.Left);
        var right = Math.Min(previous.Bounds.Right, current.Bounds.Right);

        foreach (var line in lines)
        {
            if (ReferenceEquals(line, previous) || ReferenceEquals(line, current)) continue;
            if (line.Bounds.Bottom <= top || line.Bounds.Top >= bottom) continue;
            if (line.Bounds.Right <= left || line.Bounds.Left >= right) continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether two lines are close enough vertically that one could be the next line of the other.
    /// </summary>
    /// <remarks>
    /// Deliberately the vertical-gap test of <see cref="JudgeNextLine"/> and not a second threshold
    /// beside it: this only decides which pairs are worth asking about, so a pair it lets through is
    /// still judged in full, and a pair it stops would have been refused on this very rule anyway.
    /// </remarks>
    private static bool IsWithinContinuationReach(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        var verticalGap = current.Bounds.Y - previous.Bounds.Bottom;

        return verticalGap >= -avgHeight * 0.5 && verticalGap <= Math.Max(avgHeight * 0.8, 10);
    }

    private static bool CanJoinNextLine(
        List<OcrTextBlock> group,
        OcrTextBlock current,
        IBlockAppearanceSource? appearance,
        RepeatedRowLayout repeatedRows,
        GroupTrace? trace)
    {
        var previous = group[^1];
        var solidBar = SolidLineAdvanceFor(EstablishedLeading(group));
        var (joined, rule) = JudgeNextLine(previous, current, solidBar, appearance, repeatedRows);
        if (trace is null)
            return joined;

        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        var before = appearance?.For(previous.Bounds);
        var after = appearance?.For(current.Bounds);

        trace.Decisions.Add(new GroupDecision(
            "line",
            previous.Text,
            current.Text,
            (current.Bounds.Y - previous.Bounds.Bottom) / Math.Max(1, avgHeight),
            AlignmentDelta(previous, current) / Math.Max(1, avgHeight),
            TextSizeRatio(previous, current),
            previous.Bounds.Width / Math.Max(1, current.Bounds.Width),
            joined,
            rule,
            before is null || after is null
                ? -1
                : PerceptualColor.Distance(before.Value.Background, after.Value.Background),
            before is null || after is null
                ? -1
                : PerceptualColor.Distance(before.Value.Foreground, after.Value.Foreground),
            LineAdvanceRatio(previous, current),
            solidBar));

        return joined;
    }

    /// <param name="solidBar">
    /// The most leading a pair of this group's lines may have and still be set solid. See
    /// <see cref="EstablishedLeading"/>.
    /// </param>
    private static (bool Joined, string Rule) JudgeNextLine(
        OcrTextBlock previous,
        OcrTextBlock current,
        double solidBar,
        IBlockAppearanceSource? appearance,
        RepeatedRowLayout repeatedRows)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;

        // The gap can be negative, for exactly the reason it can horizontally in CanJoinSameLine:
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
        var verticalGap = current.Bounds.Y - (previous.Bounds.Y + previous.Bounds.Height);
        if (verticalGap < -avgHeight * 0.5 || verticalGap > Math.Max(avgHeight * 0.8, 10))
            return (false, "vertical gap");

        var alignmentDelta = AlignmentDelta(previous, current);
        if (alignmentDelta > Math.Max(avgHeight * 1.2, 18))
            return (false, "alignment");

        var isTightlySet =
            LineAdvanceRatio(previous, current) <= solidBar &&
            alignmentDelta <= Math.Max(avgHeight * 0.35, 6);
        if (TextSizeRatio(previous, current) <
            (isTightlySet ? TightlySetMinTextSizeRatio : MinTextSizeRatio))
            return (false, "text size");

        var overlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Right, current.Bounds.Right) -
            Math.Max(previous.Bounds.Left, current.Bounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.Bounds.Width, current.Bounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || alignmentDelta <= Math.Max(avgHeight * 0.7, 12);
        if (!isAlignedContinuation)
            return (false, "not aligned enough to continue");

        // Layout before geometry: two entries of one list are not a wrapped sentence whatever
        // their spacing says, and their spacing routinely says they are. Asked here rather than
        // first so that the trace still reports how a pair failed when it was never a candidate.
        if (repeatedRows.AreEntriesOfOneList(previous, current))
            return (false, "repeated rows");

        return SentenceContinuationEvidence(
            previous, current, verticalGap, alignmentDelta, avgHeight, solidBar, appearance);
    }

    /// <summary>
    /// How far two lines are from sharing an edge, taking whichever of left, centre or right they
    /// agree on best. In line heights the caller normalises against, not a fixed distance.
    /// </summary>
    /// <remarks>
    /// Measuring the left edge alone reads centred text as unrelated columns. A centred line that is
    /// shorter than the one above it starts half the difference further in, so a subtitle losing a
    /// couple of words between lines moves its left edge by tens of pixels while the block itself
    /// has not moved at all — and game and film subtitles are centred more often than not. Right
    /// alignment costs nothing to include and covers the same shape mirrored.
    /// </remarks>
    private static double AlignmentDelta(OcrTextBlock previous, OcrTextBlock current)
    {
        var left = Math.Abs(previous.Bounds.Left - current.Bounds.Left);
        var right = Math.Abs(previous.Bounds.Right - current.Bounds.Right);
        var center = Math.Abs(
            (previous.Bounds.Left + previous.Bounds.Right) / 2.0 -
            (current.Bounds.Left + current.Bounds.Right) / 2.0);

        return Math.Min(left, Math.Min(center, right));
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
    /// <para>This is the argument <see cref="CanJoinSameLine"/> already makes about its own height
    /// test — box height is a property of the letters, not of whether they belong together — and the
    /// answer there was to keep the test tolerant. Here there is a better one available: the engine
    /// already measures the ink for Latin lines, because both overlays need it to size their font.
    /// On that number the same pair reads 0.937, and it is steady.</para>
    ///
    /// <para>Measured over the corpus, switching the comparison admits three pairs — the sentence
    /// above and one more wrapped English line — and separates none that were already joined. The
    /// threshold does not move; it was never the problem. See issue #79.</para>
    ///
    /// <para>CJK reports no glyph height because its box already sits on the glyphs, so those keep
    /// comparing boxes and are unaffected.</para>
    /// </remarks>
    /// <summary>
    /// The least two lines' text can differ in size and still be one wrapped sentence.
    /// </summary>
    private const double MinTextSizeRatio = 0.88;

    /// <summary>
    /// The same, for a pair already set at paragraph leading and sharing an edge.
    /// </summary>
    /// <remarks>
    /// <para>The measure this gates is a proxy with known noise: the engine derives a Latin line's
    /// glyph height from its average glyph pitch, and counts the glyphs without counting the spaces
    /// between them, so a line with more spaces reads as being set larger than its neighbour. "THAT
    /// GUY'S FAULT" over "YOU ENDED UP IN" — one hand-lettered line of one speech balloon, in one
    /// size — comes back at 0.86, and eight comic pages reached the translator in fragments because
    /// of it.</para>
    ///
    /// <para>Two points of slack, and only for pairs whose leading and alignment already say they
    /// are one block, because that is where a 12% disagreement is noise rather than a heading.
    /// Loosening it everywhere is not the same trade: measured over the corpus this admits ten
    /// wrapped sentences and nothing else, while the same bar applied to every pair also admits
    /// stat-panel rows and table cells.</para>
    ///
    /// <para>It cannot go lower than this without reaching real differences: an "X" over a balloon's
    /// "HOW" measures 0.84, and a save stamp over the character name under it measures 0.58.</para>
    /// </remarks>
    private const double TightlySetMinTextSizeRatio = 0.86;

    private static double TextSizeRatio(OcrTextBlock previous, OcrTextBlock current)
    {
        if (previous.SourceGlyphHeight is { } previousGlyph and > 0 &&
            current.SourceGlyphHeight is { } currentGlyph and > 0)
            return Math.Min(previousGlyph, currentGlyph) / Math.Max(previousGlyph, currentGlyph);

        return Math.Min(previous.Bounds.Height, current.Bounds.Height) /
               Math.Max(previous.Bounds.Height, current.Bounds.Height);
    }

    /// <summary>
    /// The leading: how far the second line sits below the first, in detection-box heights. One
    /// line advance is 1.0, so a paragraph reads a little under it and anything laid out on
    /// purpose reads well over.
    /// </summary>
    /// <remarks>
    /// <para>The distance itself is nothing new — <see cref="JudgeNextLine"/> has always measured
    /// the space between the boxes. What this changes is what it is divided by. The engine trims a
    /// CJK block's Bounds down onto its glyphs and leaves a Latin block's box as the detector drew
    /// it, so the same typographic leading comes back as two different fractions of a box: a
    /// Japanese Wikipedia paragraph reads 0.37 of a box between its lines while an English one
    /// reads 0.09. Restoring the trim puts both on the detector's box, and then they agree — every
    /// wrapped paragraph in the corpus, in all four scripts, lands between 0.67 and 1.24.</para>
    ///
    /// <para>That is what makes a leading test possible at all. Measured on box fractions the two
    /// populations overlap and there is no threshold: a checkbox list measured 0.33–0.45, which is
    /// the same band the CJK paragraphs sit in, and it was read as fifteen wrapped lines of one
    /// sentence. Measured this way the list sits at 1.33–1.45 and the paragraphs stop at 1.24,
    /// with nothing in between.</para>
    /// </remarks>
    private static double LineAdvanceRatio(OcrTextBlock previous, OcrTextBlock current)
    {
        var box = (DetectionBoxHeight(previous) + DetectionBoxHeight(current)) / 2.0;

        return box > 0 ? (current.Bounds.Y - previous.Bounds.Y) / box : -1;
    }

    /// <summary>
    /// The height of the box the detector returned, undoing the trim the engine applies to CJK.
    /// </summary>
    /// <remarks>
    /// A Latin block carries its glyph height separately and keeps the untrimmed box, so having
    /// <see cref="OcrTextBlock.SourceGlyphHeight"/> is exactly the same question as not having
    /// been trimmed. See <see cref="OnnxOcrEngine.CjkGlyphBoxScale"/>.
    /// </remarks>
    private static double DetectionBoxHeight(OcrTextBlock block) =>
        block.SourceGlyphHeight is null
            ? block.Bounds.Height / OnnxOcrEngine.CjkGlyphBoxScale
            : block.Bounds.Height;

    /// <summary>
    /// The most leading two lines can have and still be one paragraph.
    /// </summary>
    /// <remarks>
    /// Measured over the corpus: every correctly joined pair, Latin and CJK, sits at or under
    /// 1.24, and the next thing above it — a portal page's stack of separate headlines — starts at
    /// 1.27, with the checkbox list this mainly has to keep apart at 1.33 and up.
    /// </remarks>
    private const double SolidLineAdvance = 1.26;

    /// <summary>
    /// How much looser than its own established leading a paragraph's next line may sit and still
    /// be the same paragraph.
    /// </summary>
    /// <remarks>
    /// The slack is the measurement's noise, not a preference. A Latin line's detection box grows
    /// with whatever ascenders and descenders its words happen to carry, so one paragraph set at
    /// one leading comes back as a spread rather than a number: the wikinews article's body, all
    /// of it one size in one column, measures 1.05 to 1.30 between consecutive lines. Ten percent
    /// covers that spread and is the same order as the noise <see cref="TightlySetMinTextSizeRatio"/>
    /// already allows for, which arises from the same place.
    /// </remarks>
    private const double LeadingNoise = 1.10;

    /// <summary>
    /// The leading a group has already been set at, or -1 when it has not established one this can
    /// believe.
    /// </summary>
    /// <remarks>
    /// <para>The fixed bar above cannot serve every capture. A browser sets a news article's body
    /// looser than an application sets a panel, so the wikinews article wraps a sentence at 1.30
    /// while a game's checkbox list has to be refused from 1.33 — and one number cannot do both.
    /// Measured, raising the bar to 1.42 buys 9 merges across the corpus and loses 22: a portal's
    /// headline stack, a settings list and a menu all come back.</para>
    ///
    /// <para>Measuring the capture instead — the way <see cref="SameLineGapThreshold"/> measures its
    /// spacing — does not work here, and the distributions say why. A capture's leadings do not fall
    /// into two populations the way its word and item spacing does; they run continuously from 0.45
    /// to 1.9 with nothing to cut at. Worse, the statistic points the wrong way: the article that
    /// needs a looser bar has its mass at 1.0–1.3, while the portal page that must keep its tight
    /// one has its mass at 1.3–1.4, because the page is mostly the list this has to refuse.</para>
    ///
    /// <para>What the trace does say is that a paragraph knows its own leading. The pair the article
    /// missed sits at 1.30, and the lines already in the group above it were joined at 1.22 — the
    /// same paragraph, the same leading, one reading of it caught by the noise. So the bar is
    /// allowed to grow out of the group it is extending, and only out of a group whose own leading
    /// is inside the fixed bar. A list cannot exploit that: no two of its entries ever join, so no
    /// entry is ever in a group with a leading to offer, and the fixed bar is all it ever meets.</para>
    ///
    /// <para>The median rather than the last pair or the mean, so that one loose reading admitted at
    /// the edge cannot pull the bar out again behind it — a group is free to grow only while most of
    /// it is still set tight, which bounds the whole thing at <c>SolidLineAdvance * LeadingNoise</c>
    /// however many lines it gathers.</para>
    ///
    /// <para>Latin only, by the argument that justifies the slack in the first place: the spread
    /// this is here to absorb comes from Latin boxes growing with their ascenders and descenders,
    /// and a CJK box does not have it — its glyphs fill the line box, so a CJK paragraph's leading
    /// is already one number rather than a spread. Giving it slack it does not need is not free:
    /// measured, a Japanese Wikipedia page sets consecutive article summaries barely further apart
    /// than it sets the lines inside one, and two percent of extra room was enough to run two of
    /// them together into a single eight-line block.</para>
    /// </remarks>
    private static double EstablishedLeading(IReadOnlyList<OcrTextBlock> group)
    {
        if (group.Count < 2) return -1;

        var leadings = new List<double>(group.Count - 1);
        for (var i = 1; i < group.Count; i++)
        {
            if (!HasMeasurableLeading(group[i - 1], group[i])) return -1;

            leadings.Add(LineAdvanceRatio(group[i - 1], group[i]));
        }

        leadings.Sort();
        var median = leadings[leadings.Count / 2];

        return median <= SolidLineAdvance ? median : -1;
    }

    /// <inheritdoc cref="EstablishedLeading"/>
    private static double SolidLineAdvanceFor(double establishedLeading) =>
        establishedLeading > 0
            ? Math.Max(SolidLineAdvance, establishedLeading * LeadingNoise)
            : SolidLineAdvance;

    /// <summary>
    /// The same bar for the width rule, which can afford a looser one.
    /// </summary>
    /// <remarks>
    /// Looser on purpose, by the argument that keeps every set-solid threshold well inside the
    /// general ones: that rule has the leading and nothing else, while this one already knows the
    /// line above filled its column and the line below did not, so the leading is only here to
    /// rule out what the widths cannot. The measurements agree — a game panel wraps a sentence at
    /// 1.28 line advances while the settings list this was written for starts at 1.40, and nothing
    /// in the corpus falls between.
    /// </remarks>
    private const double WrappedFinalLineAdvance = 1.38;


    private static (bool Joined, string Rule) SentenceContinuationEvidence(
        OcrTextBlock previous,
        OcrTextBlock current,
        double verticalGap,
        double alignmentDelta,
        double avgHeight,
        double solidBar,
        IBlockAppearanceSource? appearance)
    {
        var previousText = previous.Text.Trim();
        var currentText = current.Text.Trim();
        if (previousText.Length == 0 || currentText.Length == 0)
            return (false, "empty text");

        // A line that opens with a bullet is the start of an item, whatever the line above it was
        // doing. Asked first because it outranks every kind of evidence below: a portal page's news
        // list sets its headlines at the leading of a paragraph — 1.22 and 1.23 line advances,
        // inside every tolerance here — and nothing but the marker says they are separate.
        if (StartsWithListMarker(currentText))
            return (false, "list marker");

        var lineAdvance = LineAdvanceRatio(previous, current);

        // A trailing colon is the one mark that reads both ways. It introduces what follows, which
        // is what a clause running onto the next line does — and equally what a form label does to
        // the field beside it. Every other mark here is lopsided enough to trust on its own: a
        // label does not end in a comma or an open bracket. So the colon alone has to show that
        // the two lines are set as one block as well, which "From row:" over "Separator Options"
        // (1.77 line advances apart) and "Column type:" over "Standard" (1.68) do not.
        if (EndsWithLabelColon(previousText))
            return lineAdvance <= solidBar
                ? (true, "punctuation")
                : (false, "label");

        if (HasUnclosedDelimiter(previousText) ||
            EndsWithContinuationPunctuation(previousText) ||
            StartsWithContinuationPunctuation(currentText))
            return (true, "punctuation");

        if (EndsWithSentenceTerminator(previousText))
            return (false, "sentence terminator");

        // A much shorter following line is a common natural wrap shape.
        // Similar-width lines without linguistic evidence are kept separate
        // so title/body pairs are not merged just because they align.
        //
        // Guarded by the length test below, because on its own that shape is also what a stack of
        // menu entries looks like — a longer label above a shorter one, aligned, evenly spaced. It
        // read 「アビリティ」over「召喚石」and 「캐릭터강화」over「소지품」as wrapped sentences, glued
        // each pair into one string for the translator, and squeezed both into one bubble. See #75.
        // The one rule here with no leading test: it asks only that the line above filled its
        // column and the line below did not, which a heading over the box it labels satisfies just
        // as well as a paragraph's last line does. Half a line of leading and a heading 1.42 times
        // the width of its content was enough to join "High Performance Serving" to "by just 1
        // command" on a product diagram — while the three cells beside it, whose headings happened
        // to be narrower than their content, stayed apart. The outcome turned on how long the
        // heading's words were, which is not a thing about the layout.
        //
        // A leading bar of its own was the obvious repair and is the wrong one: it would have to
        // sit under 0.5 to catch that pair, and a measured Korean subtitle wraps at 0.71 because
        // Hangul boxes sit tight on glyphs with no ascenders. So the question this cannot answer
        // geometrically is put to the picture instead — see VisualSplitEvidence.
        if (IsLongEnoughToHaveWrapped(previous) &&
            previous.Bounds.Width >= current.Bounds.Width * 1.35)
        {
            // The same leading bar the set-solid rule applies, and for the same reason: a
            // paragraph's last line is set at the paragraph's leading, so a pair spaced further
            // apart than that is not a paragraph ending — it is a heading over the thing it
            // labels, or the next entry in a list. This rule had no leading test at all, which is
            // how a settings panel joined "Reveal all rooms before proceeding to next floor" to
            // the unrelated checkbox under it purely because that one was shorter.
            if (HasMeasurableLeading(previous, current) && lineAdvance > WrappedFinalLineAdvance)
                return (false, "leading");

            return LooksLikeADifferentComponent(previous, current, appearance)
                ? (false, "different component")
                : (true, "shorter final line");
        }

        return IsSetSolidUnder(previous, current, alignmentDelta, avgHeight, solidBar)
            ? (true, "set solid")
            : (false, "no continuation evidence");
    }

    /// <summary>
    /// Whether the capture says these two lines belong to different components. False whenever
    /// there is no capture to ask, which is every caller that passes no appearance.
    /// </summary>
    private static bool LooksLikeADifferentComponent(
        OcrTextBlock previous, OcrTextBlock current, IBlockAppearanceSource? appearance) =>
        appearance is not null &&
        VisualSplitEvidence.IsStrong(appearance.For(previous.Bounds), appearance.For(current.Bounds));

    /// <summary>
    /// Whether the second line is set as part of the same block of text as the first — close
    /// enough, aligned tightly enough and at the same size — rather than merely lining up with it.
    /// </summary>
    /// <remarks>
    /// <para>The width rule above only admits a paragraph's <em>last</em> line, the one that ran out
    /// of text. Every line before it is about as long as the line above, because they all stopped at
    /// the same wrap boundary, so similar widths were being read as evidence against joining when
    /// they are the ordinary shape of a paragraph. A three-line subtitle reached the translator as
    /// "I never thought" / "you would actually" / "come back here." — three requests, none of which
    /// carries the sentence its neighbours needed to be translated.</para>
    ///
    /// <para>Widths are not what tells that apart from a stack of separate lines; they are similar
    /// in both. The leading is. Text that wrapped is set solid — the lines are a fraction of a line
    /// height apart because nothing but the line box put them there — while anything laid out as
    /// separate items is spaced on purpose and sits further apart. So this asks for tight leading, a
    /// shared edge to within a third of a line, and text of all but the same size, on top of the
    /// first line being long enough that running out of room is what ended it.</para>
    ///
    /// <para>The thresholds are deliberately well inside what <see cref="CanJoinNextLine"/> already
    /// allows: the general gap tolerance is 0.8 of a line and this takes 0.45, the general alignment
    /// tolerance is 1.2 and this takes 0.35. A pair that is only just close enough, or only roughly
    /// aligned, still needs the width or punctuation evidence above. That ordering is the point —
    /// joining two labels costs an invented phrase in one bubble, so this admits only the shape that
    /// nothing but wrapped text has.</para>
    ///
    /// <para>The measured menu stack of issue #75 is refused on the leading alone: 「キャラクター強化」
    /// over「所持品」sit 0.73 of a line apart, which is what laying items out on purpose looks like
    /// and is nowhere near solid.</para>
    ///
    /// <para>Text size is deliberately not tightened the same way, and asking for it undid the fix.
    /// Two rows of one paragraph, one font, rendered and read back, measured 0.91 — the glyph
    /// height carries which ascenders and descenders the words happen to have, exactly as
    /// <see cref="TextSizeRatio"/> says of box heights, and 9% is the ordinary noise of that. A
    /// stricter gate here refused the very sentence this exists to join. The 0.88 the caller already
    /// applies is the measured one, and a title over a body sits far outside it.</para>
    /// </remarks>
    private static bool IsSetSolidUnder(
        OcrTextBlock previous,
        OcrTextBlock current,
        double alignmentDelta,
        double avgHeight,
        double solidBar) =>
        (IsLongEnoughToHaveBeenSetSolid(previous) ||
            (IsLongEnoughToHaveBeenSetSolid(current) && IsInsetWithin(previous, current, avgHeight))) &&
        LineAdvanceRatio(previous, current) <= solidBar &&
        alignmentDelta <= Math.Max(avgHeight * 0.35, 6);

    /// <summary>
    /// Whether a line holds enough text to be a line of a paragraph rather than a stacked word.
    /// </summary>
    /// <remarks>
    /// Half of <see cref="WrappedLineMinAspect"/> — roughly four CJK characters or eight Latin ones.
    /// The full bar belongs to the width rule, which has only the widths to go on and so needs the
    /// first line to have plainly filled its column; here the leading has already answered that, and
    /// holding out for the full bar would refuse a Japanese subtitle for being written in a script
    /// that fits six characters where English needs twenty. What this is left to exclude is the one
    /// shape tight leading genuinely shares with wrapped text: single words stacked in a column,
    /// which stop well short of four characters' worth of line.
    /// </remarks>
    /// <summary>
    /// Whether the first line sits inset from both ends of the second — the shape of a centred
    /// balloon's opening line, and the one thing a heading over a body of text never is.
    /// </summary>
    /// <remarks>
    /// The length test above asks the first line to have plainly filled its column, which the
    /// opening line of a centred speech balloon does not: "WHY ARE" over "YOU PICKING ON AN
    /// INNOCENT" is three words on a line the artist chose to break there, and eight comic pages
    /// reached the translator as loose fragments because of it. What that test is really keeping
    /// out is a label over the thing it labels, and a label shares an edge with what it labels —
    /// "Web APIs" over "Navigation API", "Game Options" over "Link Cygames ID", both flush left.
    /// So a short line is admitted only when the line below runs past it at <em>both</em> ends,
    /// which is what centring does and what flush-left stacking cannot.
    /// </remarks>
    private static bool IsInsetWithin(OcrTextBlock line, OcrTextBlock outer, double avgHeight)
    {
        var inset = Math.Max(avgHeight * 0.2, 4);

        return line.Bounds.Left - outer.Bounds.Left >= inset &&
               outer.Bounds.Right - line.Bounds.Right >= inset;
    }

    private static bool IsLongEnoughToHaveBeenSetSolid(OcrTextBlock line) =>
        line.Bounds.Height > 0 &&
        line.Bounds.Width / line.Bounds.Height >= WrappedLineMinAspect / 2;

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

    private static bool IsLongEnoughToHaveWrapped(OcrTextBlock line) =>
        line.Bounds.Height > 0 &&
        line.Bounds.Width / line.Bounds.Height >= WrappedLineMinAspect;

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

    private static bool EndsWithLabelColon(string text) => text[^1] is '：' or ':';

    private static bool EndsWithContinuationPunctuation(string text) =>
        text[^1] is '、' or '，' or ',' or '；' or ';' or '「' or '『' or '（' or '(' or '【' or '《' or '〈';

    /// <summary>
    /// Whether the leading of this pair is worth gating a rule on, which for now means Latin.
    /// </summary>
    /// <remarks>
    /// <para>The reconstruction in <see cref="DetectionBoxHeight"/> is exact for Latin, whose box
    /// the engine leaves alone, and only approximate for CJK: the trim it undoes is the smaller of
    /// a fixed fraction and a bound taken from the glyph pitch, and when the pitch bound is the one
    /// that applied — which is precisely the case for the long lines a paragraph is made of — the
    /// box comes back too small and the leading too loose.</para>
    ///
    /// <para>The noise is not small enough to gate on. One Korean subtitle, wrapping the same
    /// sentence, measures 1.36 in one frame and 1.42 in the next, against a settings panel that
    /// has to be refused from 1.40. So the rule that already holds width evidence keeps it as its
    /// only evidence for CJK and gains the leading test where it can be trusted — which is where
    /// every case it was written for lives: an English settings list read as one paragraph, and an
    /// English product diagram's headings read as their own captions.</para>
    ///
    /// <para><see cref="IsSetSolidUnder"/> gates on it for every script regardless, because there
    /// the leading <em>is</em> the whole evidence, and measured over the corpus doing so costs one
    /// merge and buys three.</para>
    /// </remarks>
    private static bool HasMeasurableLeading(OcrTextBlock previous, OcrTextBlock current) =>
        previous.SourceGlyphHeight is not null && current.SourceGlyphHeight is not null;

    private static bool StartsWithListMarker(string text) =>
        text[0] is '·' or '・' or '•' or '‧' or '●' or '○' or '▪' or '◆' or '※';

    private static bool StartsWithContinuationPunctuation(string text) =>
        text[0] is '」' or '』' or '）' or ')' or '】' or '》' or '〉' or '、' or '，' or ',' or '。' or '！' or '？' or '!' or '?';

    private static bool EndsWithSentenceTerminator(string text) =>
        text[^1] is '。' or '！' or '？' or '!' or '?' or '.';

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
            .Where(block => block.SourceGlyphHeight.HasValue)
            .Select(block => block.SourceGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();
        double? groupGlyphHeight = glyphHeights.Count > 0 ? glyphHeights[glyphHeights.Count / 2] : null;

        return new OcrTextBlock(
            text,
            new Rect(x, y, right - x, bottom - y),
            blocks.Select(block => block.Bounds).ToList(),
            groupGlyphHeight,
            CombineConfidence(blocks));
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

    private static double? CombineGlyphHeight(double? a, double? b) =>
        (a, b) switch
        {
            (double ha, double hb) => (ha + hb) / 2.0,
            (double ha, null) => ha,
            (null, double hb) => hb,
            _ => null,
        };
}
