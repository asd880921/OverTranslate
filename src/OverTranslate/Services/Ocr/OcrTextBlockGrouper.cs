using System.Windows;

namespace OverTranslate.Services.Ocr;

internal static class OcrTextBlockGrouper
{
    public static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks) => Group(blocks, null);

    /// <param name="trace">
    /// Collects every verdict and the gap measurement behind them. Null in the app; OcrHarness
    /// passes one, because these thresholds cannot be tuned from the grouped output alone — it
    /// shows what was joined, never how close the rest came to being.
    /// </param>
    internal static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks, GroupTrace? trace)
    {
        if (blocks.Count <= 1)
        {
            // Nothing to measure and nothing to merge, but the trace still has to say so — an
            // unset threshold reads as 0.00, which is a number this never chose.
            if (trace is not null) trace.SameLineThreshold = SameLineGapThreshold.Estimate([]);
            return blocks.ToList();
        }

        var sameLineMerged = MergeSameLineFragments(blocks, trace);
        var sorted = sameLineMerged
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();

        var groups = new List<List<OcrTextBlock>>();
        foreach (var block in sorted)
        {
            var previousGroup = groups.LastOrDefault();
            if (previousGroup is not null && CanJoinNextLine(previousGroup[^1], block, trace))
                previousGroup.Add(block);
            else
                groups.Add([block]);
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
    internal readonly record struct GroupDecision(
        string Kind,
        string Previous,
        string Current,
        double Gap,
        double Fit,
        double TextSizeRatio,
        double WidthRatio,
        bool Joined,
        string Rule);

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

    private static bool CanJoinNextLine(
        OcrTextBlock previous, OcrTextBlock current, GroupTrace? trace)
    {
        var (joined, rule) = JudgeNextLine(previous, current);
        if (trace is null)
            return joined;

        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        trace.Decisions.Add(new GroupDecision(
            "line",
            previous.Text,
            current.Text,
            (current.Bounds.Y - previous.Bounds.Bottom) / Math.Max(1, avgHeight),
            AlignmentDelta(previous, current) / Math.Max(1, avgHeight),
            TextSizeRatio(previous, current),
            previous.Bounds.Width / Math.Max(1, current.Bounds.Width),
            joined,
            rule));

        return joined;
    }

    private static (bool Joined, string Rule) JudgeNextLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        if (TextSizeRatio(previous, current) < 0.88)
            return (false, "text size");

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

        var overlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Right, current.Bounds.Right) -
            Math.Max(previous.Bounds.Left, current.Bounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.Bounds.Width, current.Bounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || alignmentDelta <= Math.Max(avgHeight * 0.7, 12);
        if (!isAlignedContinuation)
            return (false, "not aligned enough to continue");

        return SentenceContinuationEvidence(previous, current, verticalGap, alignmentDelta, avgHeight);
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
    private static double TextSizeRatio(OcrTextBlock previous, OcrTextBlock current)
    {
        if (previous.SourceGlyphHeight is { } previousGlyph and > 0 &&
            current.SourceGlyphHeight is { } currentGlyph and > 0)
            return Math.Min(previousGlyph, currentGlyph) / Math.Max(previousGlyph, currentGlyph);

        return Math.Min(previous.Bounds.Height, current.Bounds.Height) /
               Math.Max(previous.Bounds.Height, current.Bounds.Height);
    }

    private static (bool Joined, string Rule) SentenceContinuationEvidence(
        OcrTextBlock previous,
        OcrTextBlock current,
        double verticalGap,
        double alignmentDelta,
        double avgHeight)
    {
        var previousText = previous.Text.Trim();
        var currentText = current.Text.Trim();
        if (previousText.Length == 0 || currentText.Length == 0)
            return (false, "empty text");

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
        if (IsLongEnoughToHaveWrapped(previous) &&
            previous.Bounds.Width >= current.Bounds.Width * 1.35)
            return (true, "shorter final line");

        return IsSetSolidUnder(previous, verticalGap, alignmentDelta, avgHeight)
            ? (true, "set solid")
            : (false, "no continuation evidence");
    }

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
        double verticalGap,
        double alignmentDelta,
        double avgHeight) =>
        IsLongEnoughToHaveBeenSetSolid(previous) &&
        verticalGap <= avgHeight * 0.45 &&
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

    private static bool EndsWithContinuationPunctuation(string text) =>
        text[^1] is '、' or '，' or ',' or '：' or ':' or '；' or ';' or '「' or '『' or '（' or '(' or '【' or '《' or '〈';

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
