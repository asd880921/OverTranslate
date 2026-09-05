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
        // Nothing reads it yet: the two fields belong to rules that arrive in later steps, and this
        // step is the wiring on its own, so that the step which does change behaviour has a corpus
        // run behind it that means something. Do not "simplify" it away.
        _ = profile;

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
            var previousGroup = groups.LastOrDefault();
            if (previousGroup is not null && CanJoinNextLine(previousGroup[^1], block, decisions))
                previousGroup.Add(block);
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
    ///   TextSizeRatio   glyph or box height ratio       unused, 0
    ///   LineAdvance     baseline advance                unused, 0
    ///   LeadingBar      the advance bar it was judged   unused, 0
    ///                   against                         
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
        double TextSizeRatio,
        double WidthRatio,
        double LineAdvance,
        double LeadingBar,
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
            // Centre, right, size, advance and the leading bar are vertical quantities. A same-line
            // verdict has no such thing to report, so they stay at zero and the harness does not
            // print them for this kind.
            0,
            0,
            0,
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
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

    private static bool CanJoinNextLine(
        OcrTextBlock previous, OcrTextBlock current, List<NextLineDecision>? decisions)
    {
        var (joined, rule) = JudgeNextLine(previous, current);
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
            TextSizeRatio(previous, current),
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
            LineAdvanceRatio(previous, current),
            WrappedFinalLineAdvance,
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
    /// The horizontal centre of a line's detection box, for the centred-text alignment diagnostic.
    /// </summary>
    private static double CenterX(OcrTextBlock line) =>
        line.LayoutBounds.X + line.LayoutBounds.Width / 2.0;

    private static (bool Joined, string Rule) JudgeNextLine(OcrTextBlock previous, OcrTextBlock current)
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

        var leftDelta = Math.Abs(previous.LayoutBounds.X - current.LayoutBounds.X);
        if (leftDelta > Math.Max(avgHeight * 1.2, 18))
            return (false, "alignment");

        var overlap = Math.Max(
            0,
            Math.Min(previous.LayoutBounds.Right, current.LayoutBounds.Right) -
            Math.Max(previous.LayoutBounds.Left, current.LayoutBounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.LayoutBounds.Width, current.LayoutBounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || leftDelta <= Math.Max(avgHeight * 0.7, 12);
        if (!isAlignedContinuation)
            return (false, "not aligned enough to continue");

        return SentenceContinuationEvidence(previous, current);
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
        OcrTextBlock previous, OcrTextBlock current)
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
