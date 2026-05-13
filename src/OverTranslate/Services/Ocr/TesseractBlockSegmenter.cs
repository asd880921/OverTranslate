using System.Windows;
using NLog;

namespace OverTranslate.Services.Ocr;

internal readonly record struct OcrWord(string Text, Rect Bounds);

internal readonly record struct OcrCluster(string Text, Rect Bounds, int RowIndex, int WordCount)
{
    public double Left => Bounds.X;
    public double Top => Bounds.Y;
    public double Right => Bounds.X + Bounds.Width;
    public double Bottom => Bounds.Y + Bounds.Height;
    public double CenterX => Bounds.X + Bounds.Width / 2.0;
}

internal static class TesseractBlockSegmenter
{
    public static List<OcrTextBlock> BuildBlocks(List<OcrWord> words, double sourceWidth, Logger? log = null)
    {
        if (words.Count == 0)
            return [];

        var rows = GroupWordsIntoRows(words);
        var clusters = SplitRowsIntoHorizontalClusters(rows, sourceWidth);
        clusters = MergeAdjacentClustersWithinRows(clusters);
        LogClusters(log, "Tesseract initial clusters", clusters);

        var mergedClusters = MergeAdjacentClustersAcrossRows(clusters);
        LogClusters(log, "Tesseract merged clusters", mergedClusters);

        return mergedClusters
            .Select(c => new OcrTextBlock(c.Text, c.Bounds))
            .ToList();
    }

    internal static List<List<OcrWord>> GroupWordsIntoRows(List<OcrWord> words)
    {
        var sorted = words
            .OrderBy(w => w.Bounds.Y)
            .ThenBy(w => w.Bounds.X)
            .ToList();

        var rows = new List<List<OcrWord>>();
        foreach (var word in sorted)
        {
            bool added = false;
            foreach (var row in rows)
            {
                double rowTop = row.Min(w => w.Bounds.Y);
                double rowBottom = row.Max(w => w.Bounds.Y + w.Bounds.Height);
                double wordBottom = word.Bounds.Y + word.Bounds.Height;
                if (word.Bounds.Y < rowBottom && wordBottom > rowTop)
                {
                    row.Add(word);
                    added = true;
                    break;
                }
            }

            if (!added)
                rows.Add([word]);
        }

        return rows;
    }

    internal static List<OcrCluster> SplitRowsIntoHorizontalClusters(List<List<OcrWord>> rows, double sourceWidth)
    {
        var clusters = new List<OcrCluster>();

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowSorted = rows[rowIndex]
                .OrderBy(w => w.Bounds.X)
                .ToList();

            double avgHeight = rowSorted.Average(w => w.Bounds.Height);
            bool mostlyCjk = IsMostlyCjkText(JoinTokens(rowSorted.Select(w => w.Text)));
            double gapThreshold = mostlyCjk
                ? Math.Min(Math.Max(avgHeight * 2.4, 28), Math.Max(sourceWidth * 0.12, 32))
                : Math.Max(avgHeight * 1.5, 20);

            var clusterWords = new List<OcrWord> { rowSorted[0] };
            for (int i = 1; i < rowSorted.Count; i++)
            {
                var prev = rowSorted[i - 1];
                var curr = rowSorted[i];
                double gap = curr.Bounds.X - (prev.Bounds.X + prev.Bounds.Width);
                if (gap > gapThreshold)
                {
                    clusters.Add(BuildCluster(clusterWords, rowIndex));
                    clusterWords = [];
                }

                clusterWords.Add(curr);
            }

            if (clusterWords.Count > 0)
                clusters.Add(BuildCluster(clusterWords, rowIndex));
        }

        return clusters;
    }

    internal static List<OcrCluster> MergeAdjacentClustersWithinRows(List<OcrCluster> clusters)
    {
        if (clusters.Count <= 1)
            return clusters;

        var merged = clusters
            .OrderBy(c => c.RowIndex)
            .ThenBy(c => c.Left)
            .ToList();

        for (int i = 1; i < merged.Count; i++)
        {
            var previous = merged[i - 1];
            var current = merged[i];
            if (current.RowIndex != previous.RowIndex)
                continue;

            if (!ShouldMergeWithinRow(previous, current))
                continue;

            merged[i - 1] = Merge(previous, current);
            merged.RemoveAt(i);
            i--;
        }

        return merged;
    }

    internal static List<OcrCluster> MergeAdjacentClustersAcrossRows(List<OcrCluster> clusters)
    {
        if (clusters.Count <= 1)
            return clusters;

        var merged = clusters
            .OrderBy(c => c.RowIndex)
            .ThenBy(c => c.Left)
            .ToList();

        for (int i = 1; i < merged.Count; i++)
        {
            var previous = merged[i - 1];
            var current = merged[i];

            if (current.RowIndex == previous.RowIndex)
                continue;

            if (!ShouldMerge(previous, current))
                continue;

            merged[i - 1] = Merge(previous, current);
            merged.RemoveAt(i);
            i--;
        }

        return merged;
    }

    internal static bool ShouldMerge(OcrCluster previous, OcrCluster current)
    {
        bool tinyTail =
            current.WordCount <= 2 &&
            current.Text.Length <= 4 &&
            current.Bounds.Width <= previous.Bounds.Width * 0.25;

        if (!tinyTail)
            return false;

        double rowHeightRef = Math.Min(previous.Bounds.Height, current.Bounds.Height);
        double verticalGap = current.Top - previous.Bottom;
        if (verticalGap < 0 || verticalGap > Math.Max(rowHeightRef * 0.9, 10))
            return false;

        double overlap = Math.Max(0, Math.Min(previous.Right, current.Right) - Math.Max(previous.Left, current.Left));
        double overlapRate = overlap / Math.Max(1, Math.Min(previous.Bounds.Width, current.Bounds.Width));
        bool xOverlap = overlapRate >= 0.45;
        bool leftAligned = Math.Abs(previous.Left - current.Left) <= Math.Max(rowHeightRef * 0.8, 12);
        bool centerAligned = Math.Abs(previous.CenterX - current.CenterX) <= Math.Max(Math.Max(previous.Bounds.Width, current.Bounds.Width) * 0.2, 18);

        if (!xOverlap && !leftAligned && !centerAligned)
            return false;

        bool currentLooksLikeContinuation =
            LooksLikeContinuationText(current.Text) ||
            EndsWithContinuation(previous.Text);

        if (!currentLooksLikeContinuation)
            return false;

        double unionLeft = Math.Min(previous.Left, current.Left);
        double unionRight = Math.Max(previous.Right, current.Right);
        double unionWidth = unionRight - unionLeft;
        double allowedWidth = Math.Max(previous.Bounds.Width, current.Bounds.Width) + Math.Max(rowHeightRef * 3, 40);
        if (unionWidth > allowedWidth && !xOverlap)
            return false;

        return true;
    }

    internal static bool ShouldMergeWithinRow(OcrCluster previous, OcrCluster current)
    {
        if (!IsMostlyCjkText(previous.Text + current.Text))
            return false;

        double avgHeight = (previous.Bounds.Height + current.Bounds.Height) / 2.0;
        double gap = current.Left - previous.Right;
        if (gap < 0 || gap > Math.Max(avgHeight * 3.8, 56))
            return false;

        double verticalOffset = Math.Abs(previous.Top - current.Top);
        if (verticalOffset > Math.Max(avgHeight * 0.35, 8))
            return false;

        bool smallTail =
            current.WordCount <= 3 ||
            current.Text.Length <= 6 ||
            current.Bounds.Width <= previous.Bounds.Width * 0.35;

        bool openerCarry =
            EndsWithContinuation(previous.Text) ||
            StartsWithContinuation(current.Text);

        return smallTail || openerCarry;
    }

    private static bool LooksLikeContinuationText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Length <= 4)
            return true;

        char first = trimmed[0];
        char last = trimmed[^1];

        return first is '」' or '』' or ')' or '）' or '】' or ']' ||
               last is '「' or '『' or '(' or '（' or '【' or '[' ||
               last is '、' or '。' or '，' or ',' or '.' or '」' or '』' or '!' or '?' ||
               first is '・' or 'ー';
    }

    private static bool EndsWithContinuation(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        char last = trimmed[^1];
        return last is '「' or '『' or '(' or '（' or '【' or '[' or '・' or 'ー';
    }

    private static bool StartsWithContinuation(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return false;

        char first = trimmed[0];
        return first is '」' or '』' or ')' or '）' or '】' or ']' or '、' or '。' or '，' or ',' or '.' or '!' or '?' or 'ー';
    }

    private static OcrCluster Merge(OcrCluster previous, OcrCluster current)
    {
        var mergedText = JoinTokens(previous.Text, current.Text);
        double x = Math.Min(previous.Left, current.Left);
        double y = Math.Min(previous.Top, current.Top);
        double right = Math.Max(previous.Right, current.Right);
        double bottom = Math.Max(previous.Bottom, current.Bottom);

        return new OcrCluster(
            mergedText,
            new Rect(x, y, right - x, bottom - y),
            previous.RowIndex,
            previous.WordCount + current.WordCount);
    }

    private static OcrCluster BuildCluster(List<OcrWord> words, int rowIndex)
    {
        var text = JoinTokens(words.Select(w => w.Text));
        double x = words.Min(w => w.Bounds.X);
        double y = words.Min(w => w.Bounds.Y);
        double right = words.Max(w => w.Bounds.X + w.Bounds.Width);
        double bottom = words.Max(w => w.Bounds.Y + w.Bounds.Height);

        return new OcrCluster(
            text,
            new Rect(x, y, right - x, bottom - y),
            rowIndex,
            words.Count);
    }

    private static void LogClusters(Logger? log, string message, List<OcrCluster> clusters)
    {
        if (log == null || !log.IsDebugEnabled)
            return;

        for (int i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            log.Debug(
                "{Message} [{Index}] row={Row} words={Words} bounds=({X:0.#},{Y:0.#},{W:0.#},{H:0.#}) text=\"{Text}\"",
                message,
                i,
                cluster.RowIndex,
                cluster.WordCount,
                cluster.Bounds.X,
                cluster.Bounds.Y,
                cluster.Bounds.Width,
                cluster.Bounds.Height,
                cluster.Text);
        }
    }

    private static string JoinTokens(IEnumerable<string> tokens)
    {
        var parts = tokens
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (parts.Count == 0)
            return string.Empty;

        string text = parts[0];
        for (int i = 1; i < parts.Count; i++)
            text = JoinTokens(text, parts[i]);

        return text;
    }

    private static string JoinTokens(string left, string right)
    {
        var trimmedLeft = left.TrimEnd();
        var trimmedRight = right.TrimStart();
        if (trimmedLeft.Length == 0) return trimmedRight;
        if (trimmedRight.Length == 0) return trimmedLeft;

        return NeedsSpaceBetween(trimmedLeft[^1], trimmedRight[0])
            ? $"{trimmedLeft} {trimmedRight}"
            : $"{trimmedLeft}{trimmedRight}";
    }

    private static bool NeedsSpaceBetween(char left, char right)
    {
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;

        if (IsNoSpacePunctuation(left) || IsNoSpacePunctuation(right))
            return false;

        bool leftCjk = IsCjkLike(left);
        bool rightCjk = IsCjkLike(right);
        if (leftCjk && rightCjk)
            return false;

        if (leftCjk || rightCjk)
            return false;

        return true;
    }

    private static bool IsNoSpacePunctuation(char c) =>
        c is '、' or '。' or '，' or '．' or '・' or '：' or '；' or '！' or '？' or
            '「' or '」' or '『' or '』' or '（' or '）' or '【' or '】' or '〈' or '〉' or
            '(' or ')' or '[' or ']' or '{' or '}' or '"' or '\'' or ',' or '.' or ':' or ';' or '!' or '?';

    private static bool IsCjkLike(char c)
    {
        int code = c;
        return
            (code >= 0x3040 && code <= 0x309F) || // Hiragana
            (code >= 0x30A0 && code <= 0x30FF) || // Katakana
            (code >= 0x31F0 && code <= 0x31FF) || // Katakana Phonetic Extensions
            (code >= 0x3400 && code <= 0x4DBF) || // CJK Extension A
            (code >= 0x4E00 && code <= 0x9FFF) || // CJK Unified Ideographs
            (code >= 0xF900 && code <= 0xFAFF) || // CJK Compatibility Ideographs
            (code >= 0xAC00 && code <= 0xD7AF) || // Hangul Syllables
            (code >= 0x1100 && code <= 0x11FF) || // Hangul Jamo
            (code >= 0x3130 && code <= 0x318F);   // Hangul Compatibility Jamo
    }

    private static bool IsMostlyCjkText(string text)
    {
        int letterCount = 0;
        int cjkCount = 0;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
                continue;

            letterCount++;
            if (IsCjkLike(c))
                cjkCount++;
        }

        if (letterCount == 0)
            return false;

        return cjkCount >= letterCount * 0.6;
    }
}
