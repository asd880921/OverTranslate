using System.Drawing;
using System.Windows;
using NLog;

namespace OverTranslate.Services.Ocr;

internal static class MixedOrientationOcr
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const double VerticalAspectRatio = 1.5;
    private const double DuplicateOverlap = 0.55;
    private const double ConfidenceTolerance = 0.12;

    public static bool UsesVerticalPass(string sourceLanguage) =>
        OcrLanguageRouter.Normalize(sourceLanguage) is "AUTO" or "ZH" or "ZH-HANT" or "JA";

    public static async Task<List<OcrTextBlock>> RecognizeAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var horizontalTask = RecognizeAndGroupAsync(
            engine, bitmap, sourceLanguage, cancellationToken);
        if (!UsesVerticalPass(sourceLanguage))
            return await horizontalTask.ConfigureAwait(false);

        // This is the proven path from the retired batch-image feature: turning the whole image
        // makes vertical columns horizontal before detection. Candidate-based retry cannot help
        // when the detector returned no upright blocks in the first place.
        using var rotated = new Bitmap(bitmap);
        rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);
        var verticalTask = RecognizeAndGroupAsync(
            engine, rotated, sourceLanguage, cancellationToken);

        // Wait for both before the rotated bitmap is disposed. The primary pass still owns the
        // result: if it fails, propagate its error; if only the supplemental pass fails, preserve
        // the horizontal result instead of losing a screenshot that was already readable.
        try
        {
            await Task.WhenAll(horizontalTask, verticalTask).ConfigureAwait(false);
        }
        catch
        {
            // Each task is awaited below so cancellation and the primary exception retain their
            // original stack and semantics.
        }

        var horizontal = await horizontalTask.ConfigureAwait(false);
        List<OcrTextBlock> rotatedBlocks;
        try
        {
            rotatedBlocks = await verticalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "直排補充 OCR 失敗，保留原始橫排辨識結果。");
            return horizontal;
        }

        var vertical = rotatedBlocks
            .Select(block => MapBack(block, bitmap.Width))
            .Where(IsVerticalResult)
            .ToList();
        var merged = Merge(horizontal, vertical);

        Log.Info(
            "Mixed OCR horizontal={Horizontal} rotated={Rotated} vertical={Vertical} merged={Merged}",
            horizontal.Count,
            rotatedBlocks.Count,
            vertical.Count,
            merged.Count);
        return merged;
    }

    public static OcrTextBlock MapBack(OcrTextBlock block, int originalWidth)
    {
        var mappedBounds = MapBack(block.Bounds, originalWidth);
        return block with
        {
            Bounds = mappedBounds,
            SourceLineBounds = block.SourceLineBounds?
                .Select(line => MapBack(line, originalWidth))
                .ToList(),
            // After mapping, Bounds is a tall column. The line height before mapping becomes the
            // column width and is the source glyph size the horizontal overlay should use.
            SourceGlyphHeight = block.Bounds.Height,
        };
    }

    public static List<OcrTextBlock> Merge(
        IReadOnlyList<OcrTextBlock> horizontal,
        IReadOnlyList<OcrTextBlock> vertical)
    {
        if (vertical.Count == 0)
            return horizontal.ToList();

        var merged = horizontal.ToList();
        foreach (var candidate in vertical)
        {
            var duplicates = merged
                .Select((block, index) => (Block: block, Index: index))
                .Where(item => OverlapOfSmaller(item.Block.Bounds, candidate.Bounds) >= DuplicateOverlap)
                .ToList();

            if (duplicates.Count == 0)
            {
                merged.Add(candidate);
                continue;
            }

            if (!PreferVertical(candidate, duplicates.Select(item => item.Block).ToList()))
                continue;

            foreach (var duplicate in duplicates.OrderByDescending(item => item.Index))
                merged.RemoveAt(duplicate.Index);
            merged.Add(candidate);
        }

        return merged
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();
    }

    private static async Task<List<OcrTextBlock>> RecognizeAndGroupAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var blocks = await engine
            .RecognizeAsync(bitmap, sourceLanguage, cancellationToken)
            .ConfigureAwait(false);
        return OcrTextBlockGrouper.Group(blocks);
    }

    private static Rect MapBack(Rect rotated, int originalWidth) => new(
        originalWidth - rotated.Bottom,
        rotated.X,
        rotated.Height,
        rotated.Width);

    private static bool IsVerticalResult(OcrTextBlock block) =>
        block.Bounds.Width >= 2 &&
        block.Bounds.Height >= 24 &&
        block.Bounds.Height >= block.Bounds.Width * VerticalAspectRatio &&
        EffectiveCharacters(block.Text) >= 2;

    private static bool PreferVertical(
        OcrTextBlock vertical,
        IReadOnlyList<OcrTextBlock> horizontal)
    {
        var verticalCharacters = EffectiveCharacters(vertical.Text);
        var horizontalCharacters = horizontal.Sum(block => EffectiveCharacters(block.Text));
        var verticalConfidence = vertical.Confidence;
        var horizontalConfidence = CombinedConfidence(horizontal);

        if (verticalCharacters < horizontalCharacters)
            return false;

        if (verticalConfidence is { } verticalScore &&
            horizontalConfidence is { } horizontalScore &&
            verticalScore + ConfidenceTolerance < horizontalScore)
            return false;

        return verticalCharacters > horizontalCharacters ||
               verticalConfidence is not null ||
               horizontalConfidence is null;
    }

    private static double? CombinedConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        double weighted = 0;
        double weight = 0;
        foreach (var block in blocks)
        {
            if (block.Confidence is not { } confidence)
                continue;

            var characters = Math.Max(1, EffectiveCharacters(block.Text));
            weighted += confidence * characters;
            weight += characters;
        }

        return weight > 0 ? weighted / weight : null;
    }

    private static int EffectiveCharacters(string text) =>
        text.Count(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character));

    private static double OverlapOfSmaller(Rect first, Rect second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        var smallerArea = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return smallerArea > 0 ? width * height / smallerArea : 0;
    }
}
