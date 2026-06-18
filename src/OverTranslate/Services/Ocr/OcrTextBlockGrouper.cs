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
        var sorted = blocks
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();
        var merged = new List<OcrTextBlock>();

        foreach (var block in sorted)
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && CanJoinSameLine(previous, block))
                merged[^1] = MergeSameLine(previous, block);
            else
                merged.Add(block);
        }

        return merged;
    }

    private static bool CanJoinSameLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;

        // The detector splits a spaced/large Latin line (e.g. a heading) into per-word boxes that
        // must be re-merged here. Latin word boxes wrap the ink tightly, so their height AND
        // vertical position swing with ascenders/descenders: a no-descender word ("Take",
        // "Translate") yields a shorter box sitting cap→baseline, while a descender word ("your",
        // "learning") yields a taller box sitting x-height→descender. The old 0.88 height ratio and
        // 0.72 overlap thresholds rejected exactly these pairs, scattering one phrase into
        // separately translated words. CJK is unaffected (uniform full-height glyph boxes), so the
        // loosened bounds stay well clear of real CJK same-line cases. The horizontal-gap guard
        // below remains the primary defence against merging unrelated neighbours.
        var heightRatio = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                          Math.Max(previous.Bounds.Height, current.Bounds.Height);
        if (heightRatio < 0.6)
            return false;

        var verticalOverlap = Math.Max(
            0,
            Math.Min(previous.Bounds.Bottom, current.Bounds.Bottom) -
            Math.Max(previous.Bounds.Top, current.Bounds.Top));
        var verticalOverlapRate = verticalOverlap /
                                  Math.Max(1, Math.Min(previous.Bounds.Height, current.Bounds.Height));
        if (verticalOverlapRate < 0.4)
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
            SourceGlyphHeight: CombineGlyphHeight(previous.SourceGlyphHeight, current.SourceGlyphHeight));
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
        var heightRatio = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                          Math.Max(previous.Bounds.Height, current.Bounds.Height);
        if (heightRatio < 0.88)
            return false;

        var verticalGap = current.Bounds.Y - (previous.Bounds.Y + previous.Bounds.Height);
        if (verticalGap < 0 || verticalGap > Math.Max(avgHeight * 0.8, 10))
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
        return previous.Bounds.Width >= current.Bounds.Width * 1.35;
    }

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
            groupGlyphHeight);
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
