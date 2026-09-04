using System.Windows;

namespace OverTranslate.Services.Ocr;

internal static class OcrTextBlockGrouper
{
    public static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks)
    {
        if (blocks.Count <= 1)
            return blocks.ToList();

        var sameLineMerged = MergeSameLineFragments(blocks);
        var sorted = sameLineMerged
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();

        var groups = new List<List<OcrTextBlock>>();
        foreach (var block in sorted)
        {
            var previousGroup = groups.LastOrDefault();
            if (previousGroup is not null && CanJoinNextLine(previousGroup[^1], block))
                previousGroup.Add(block);
            else
                groups.Add([block]);
        }

        return groups.Select(BuildGroup).ToList();
    }

    private static List<OcrTextBlock> MergeSameLineFragments(IReadOnlyList<OcrTextBlock> blocks)
    {
        // Process left-to-right and append each fragment to whichever open row it continues, rather
        // than a global Y-then-X sort + "compare only with the previous" merge. Latin word boxes on
        // one line have tops that vary with ascenders/descenders (e.g. "Send" y=32 vs "to" y=37), so
        // a Y-primary sort interleaves words from across the line and the sequential merge then
        // leaves a single line scattered into separately translated words. Sorting by X keeps a
        // line's fragments in reading order; the vertical-overlap test in CanJoinSameLine routes
        // each fragment to the correct row when several lines are present.
        var ordered = blocks
            .OrderBy(block => block.Bounds.X)
            .ThenBy(block => block.Bounds.Y)
            .ToList();
        var merged = new List<OcrTextBlock>();

        foreach (var block in ordered)
        {
            var targetIndex = FindSameLineTarget(merged, block);
            if (targetIndex >= 0)
                merged[targetIndex] = MergeSameLine(merged[targetIndex], block);
            else
                merged.Add(block);
        }

        return merged;
    }

    // Among the open row-blocks, returns the one this fragment continues — the candidate to its left
    // with the most vertical overlap (best row match), or -1 when none qualifies.
    private static int FindSameLineTarget(List<OcrTextBlock> merged, OcrTextBlock block)
    {
        var bestIndex = -1;
        var bestOverlap = double.NegativeInfinity;

        for (var i = 0; i < merged.Count; i++)
        {
            var candidate = merged[i];
            if (!CanJoinSameLine(candidate, block))
                continue;

            var overlap = Math.Min(candidate.Bounds.Bottom, block.Bounds.Bottom) -
                          Math.Max(candidate.Bounds.Top, block.Bounds.Top);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static bool CanJoinSameLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;

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
        // to the line. Discriminating between lines is the vertical-overlap test's job below, as the
        // note above says; this only has to keep out boxes of wildly different size.
        var heightRatio = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                          Math.Max(previous.Bounds.Height, current.Bounds.Height);
        if (heightRatio < 0.5)
            return false;

        var verticalOverlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Bottom, current.Bounds.Bottom) -
            Math.Max(previous.Bounds.Top, current.Bounds.Top));
        var verticalOverlapRate = verticalOverlap /
                                  Math.Max(1, Math.Min(previous.Bounds.Height, current.Bounds.Height));
        if (verticalOverlapRate < 0.72)
            return false;

        // The gap can be negative: on large captures the detector's unclip expansion enlarges big
        // heading word-boxes until adjacent ones overlap horizontally (e.g. "Translate" right=533
        // vs "your website" left=515 → gap -18), which the old `>= 0` guard rejected, scattering
        // one heading into word-by-word translations. Allow up to a line-height of overlap; the
        // vertical-overlap and height-ratio checks above already keep stacked/unrelated lines out.
        var horizontalGap = current.Bounds.X - previous.Bounds.Right;
        return horizontalGap >= -avgHeight && horizontalGap <= Math.Max(avgHeight * 1.35, 18);
    }

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
            RenderGlyphHeight: CombineGlyphHeight(previous.RenderGlyphHeight, current.RenderGlyphHeight),
            Confidence: CombineConfidence([previous, current]),
            LayoutScript: LayoutScriptDetection.For(text),
            LayoutBounds: Rect.Union(previous.LayoutBounds, current.LayoutBounds));
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

    private static bool CanJoinNextLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        if (TextSizeRatio(previous, current) < 0.88)
            return false;

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
            return false;

        var leftDelta = Math.Abs(previous.Bounds.X - current.Bounds.X);
        if (leftDelta > Math.Max(avgHeight * 1.2, 18))
            return false;

        var overlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Right, current.Bounds.Right) -
            Math.Max(previous.Bounds.Left, current.Bounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.Bounds.Width, current.Bounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || leftDelta <= Math.Max(avgHeight * 0.7, 12);
        return isAlignedContinuation && HasSentenceContinuationEvidence(previous, current);
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
        if (previous.RenderGlyphHeight is { } previousGlyph and > 0 &&
            current.RenderGlyphHeight is { } currentGlyph and > 0)
            return Math.Min(previousGlyph, currentGlyph) / Math.Max(previousGlyph, currentGlyph);

        return Math.Min(previous.Bounds.Height, current.Bounds.Height) /
               Math.Max(previous.Bounds.Height, current.Bounds.Height);
    }

    private static bool HasSentenceContinuationEvidence(OcrTextBlock previous, OcrTextBlock current)
    {
        var previousText = previous.Text.Trim();
        var currentText = current.Text.Trim();
        if (previousText.Length == 0 || currentText.Length == 0)
            return false;

        if (HasUnclosedDelimiter(previousText) ||
            EndsWithContinuationPunctuation(previousText) ||
            StartsWithContinuationPunctuation(currentText))
            return true;

        if (EndsWithSentenceTerminator(previousText))
            return false;

        // A much shorter following line is a common natural wrap shape.
        // Similar-width lines without linguistic evidence are kept separate
        // so title/body pairs are not merged just because they align.
        //
        // Guarded by the length test below, because on its own that shape is also what a stack of
        // menu entries looks like — a longer label above a shorter one, aligned, evenly spaced. It
        // read 「アビリティ」over「召喚石」and 「캐릭터강화」over「소지품」as wrapped sentences, glued
        // each pair into one string for the translator, and squeezed both into one bubble. See #75.
        return IsLongEnoughToHaveWrapped(previous) &&
               previous.Bounds.Width >= current.Bounds.Width * 1.35;
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
            .Where(block => block.RenderGlyphHeight.HasValue)
            .Select(block => block.RenderGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();
        double? groupGlyphHeight = glyphHeights.Count > 0 ? glyphHeights[glyphHeights.Count / 2] : null;

        return new OcrTextBlock(
            text,
            new Rect(x, y, right - x, bottom - y),
            blocks.Select(block => block.Bounds).ToList(),
            groupGlyphHeight,
            CombineConfidence(blocks),
            // Re-read rather than folded together: the field's contract is "the script of this
            // block's own text", and a group whose lines are Latin and CJK is exactly the Mixed
            // that the joined text reports.
            LayoutScriptDetection.For(text),
            blocks.Select(block => block.LayoutBounds).Aggregate(Rect.Union));
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
