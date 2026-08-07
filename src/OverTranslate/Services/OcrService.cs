using System.Drawing;
using NLog;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(
    string Text,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    // Visual glyph height (physical px) used to size the overlay font, kept separate from
    // Bounds. For Latin source the detection box is much taller than the rendered CJK font,
    // so Bounds stays full (for background coverage) while this drives only the font size.
    // Null for CJK, where Bounds already matches the glyph height.
    double? SourceGlyphHeight = null,
    // Mean per-character recognition confidence, 0–1. Reading the same unchanged text twice
    // gives two slightly different answers, and this is what says which to believe; null when
    // the engine reported no scores.
    double? Confidence = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];
}

public class OcrService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly OnnxOcrEngine _engine = new();

    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (OcrLanguageRouter.IsSupported(sourceLanguage))
            return RecognizeAndGroupAsync(
                _engine, bitmap, OcrLanguageRouter.Normalize(sourceLanguage), cancellationToken);

        throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));
    }

    /// <summary>
    /// Recognises only if the engine has a free slot right now, returning null instead of queueing.
    /// For callers watching a live screen, where a queued pass would be answering a frame that has
    /// already been replaced — see <see cref="IOcrEngine.TryRecognizeAsync"/>.
    /// </summary>
    public async Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        var blocks = await _engine.TryRecognizeAsync(
            bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken);

        return blocks is null ? null : OcrTextBlockGrouper.Group(blocks);
    }

    /// <summary>
    /// Keeps the loaded model in memory while a continuous caller is running — see
    /// <see cref="OnnxOcrEngine.SetKeepWarm"/>.
    /// </summary>
    public void SetKeepWarm(bool keepWarm) => _engine.SetKeepWarm(keepWarm);

    public void Dispose()
    {
        _engine.Dispose();
    }

    private static async Task<List<OcrTextBlock>> RecognizeAndGroupAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var blocks = await engine.RecognizeAsync(bitmap, sourceLanguage, cancellationToken);
        await RereadDoubtfulLinesAsync(engine, bitmap, sourceLanguage, blocks, cancellationToken);
        return OcrTextBlockGrouper.Group(blocks);
    }

    /// <summary>
    /// Reads the least confident lines a second time, on their own, and keeps whichever reading came
    /// out better. Grouping happens afterwards, so a line replaced here is still joined to its
    /// neighbours normally.
    /// </summary>
    /// <remarks>
    /// The screenshot flow only ever looks at a capture once, so a line read badly is read badly for
    /// good — where the realtime loop sees the same subtitle several times a second and can simply
    /// keep its best reading. This buys the same thing for a single look, for the price of a few
    /// extra line-sized inferences. See <see cref="DoubtfulBlocks"/> for which lines and why.
    ///
    /// Concurrent, because the engine already admits several inferences at once and has the measured
    /// bound for it; on a machine with too few cores that bound is one and these queue instead. A
    /// second engine is emphatically not the answer — it would load a second copy of the models.
    /// </remarks>
    private static async Task RereadDoubtfulLinesAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        List<OcrTextBlock> blocks,
        CancellationToken cancellationToken)
    {
        var doubtful = DoubtfulBlocks.Select(blocks);
        if (doubtful.Count == 0) return;

        var rereads = doubtful.Select(index => RereadAsync(index)).ToArray();
        var improved = (await Task.WhenAll(rereads)).Count(better => better);

        Log.Info(
            "Re-read {Count} doubtful line(s) of {Total}; {Improved} came back better",
            doubtful.Count, blocks.Count, improved);

        async Task<bool> RereadAsync(int index)
        {
            var original = blocks[index];
            var crop = DoubtfulBlocks.CropAround(original.Bounds, bitmap.Width, bitmap.Height);
            if (crop.Width <= 0 || crop.Height <= 0) return false;

            List<OcrTextBlock> found;
            try
            {
                // Cloned rather than shared: GDI+ will not read one bitmap from several threads.
                using var slice = bitmap.Clone(crop, bitmap.PixelFormat);
                found = await engine.RecognizeAsync(slice, sourceLanguage, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A line that cannot be re-read keeps the reading it already had.
                Log.Warn(ex, "Re-reading a doubtful line failed; keeping the original reading");
                return false;
            }

            var lines = found
                .Where(block => DoubtfulBlocks.IsSameLine(original.Bounds, block.Bounds, crop))
                .ToList();
            if (lines.Count == 0) return false;

            var text = string.Join(" ", lines.Select(line => line.Text.Trim()).Where(t => t.Length > 0));
            var confidence = lines.Min(line => line.Confidence ?? 1.0);
            if (text.Length == 0 || confidence <= (original.Confidence ?? 0)) return false;

            Log.Debug(
                "Re-read improved a line: {Old:0.00} \"{OldText}\" -> {New:0.00} \"{NewText}\"",
                original.Confidence ?? 0, original.Text, confidence, text);

            // Only the words change. The geometry stays as the full capture measured it, which is
            // what the overlay lays out against and what the background sampler works from.
            blocks[index] = original with { Text = text, Confidence = confidence };
            return true;
        }
    }
}
