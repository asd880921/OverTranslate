using System.Drawing;
using System.Windows;
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
    double? SourceGlyphHeight = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];
}

public class OcrService : IDisposable
{
    private readonly OnnxOcrEngine _engine = new();

    /// <param name="verticalText">
    /// Set for pages written top-to-bottom, as Japanese comics are. The detector only finds
    /// horizontal runs, so on a vertical page it returns almost nothing and the few boxes it does
    /// return come back as gibberish. Turning the page a quarter turn first fixes both — measured
    /// on a sample page, 2 unusable blocks became 21 correct ones — with no change to any model or
    /// threshold, so the default path behaves exactly as it always has.
    /// </param>
    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        bool verticalText = false)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        var language = OcrLanguageRouter.Normalize(sourceLanguage);

        return verticalText
            ? RecognizeVerticalAsync(_engine, bitmap, language, cancellationToken)
            : RecognizeAndGroupAsync(_engine, bitmap, language, cancellationToken);
    }

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
        return OcrTextBlockGrouper.Group(blocks);
    }

    /// <summary>
    /// Reads a vertical page by turning it a quarter turn anticlockwise, running the ordinary
    /// pipeline, then mapping the results back. Anticlockwise specifically: clockwise finds
    /// nothing, and this direction also yields the columns in reading order, because the rightmost
    /// column — where a reader starts — ends up at the top.
    /// </summary>
    private static async Task<List<OcrTextBlock>> RecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        // Grouping runs while the page is still turned, so adjacent columns merge under the same
        // left-to-right rules that already work for horizontal text.
        using var rotated = new Bitmap(bitmap);
        rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);

        var blocks = await RecognizeAndGroupAsync(engine, rotated, sourceLanguage, cancellationToken);

        var columns = blocks.Select(block => block with
        {
            Bounds = MapBack(block.Bounds, bitmap.Width),
            SourceLineBounds = null,
            // Turned back, a column is tall and narrow, so its width — not its height — is the size
            // the glyphs were actually drawn at.
            SourceGlyphHeight = block.Bounds.Height,
        }).ToList();

        return MergeColumns(columns);
    }

    /// <summary>
    /// Joins the columns of one speech balloon back into a single block, right to left.
    /// <para>
    /// Each column comes out of the detector on its own, which leaves the translation squeezed into
    /// a strip barely one glyph wide. Columns of the same balloon share a top edge — vertical text
    /// starts at the top — and sit side by side, so that pair of facts separates them from the
    /// next balloon reliably enough to merge on.
    /// </para>
    /// <para>
    /// The merged block carries its columns as line bounds, which is what puts it on the layout's
    /// existing multi-line path: the translation then wraps across the balloon's full width instead
    /// of down a single column.
    /// </para>
    /// </summary>
    private static List<OcrTextBlock> MergeColumns(List<OcrTextBlock> columns)
    {
        // Right to left is the reading order, so this is also the order the text joins in.
        var remaining = columns.OrderByDescending(column => column.Bounds.X).ToList();
        var merged = new List<OcrTextBlock>();

        while (remaining.Count > 0)
        {
            var group = new List<OcrTextBlock> { remaining[0] };
            remaining.RemoveAt(0);

            // A balloon is a chain: each column only has to touch one already in the group, or a
            // four-column balloon would never gather past its second column.
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (!group.Any(member => SameBalloon(member, remaining[i])))
                        continue;

                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    grew = true;
                }
            }

            merged.Add(Combine(group));
        }

        return merged;
    }

    private static bool SameBalloon(OcrTextBlock a, OcrTextBlock b)
    {
        double columnWidth = Math.Max(a.Bounds.Width, b.Bounds.Width);

        // Columns of one balloon start level with each other; a different balloon that happens to
        // sit alongside almost never does.
        if (Math.Abs(a.Bounds.Y - b.Bounds.Y) > columnWidth * 0.6)
            return false;

        double gap = Math.Max(a.Bounds.Left, b.Bounds.Left) - Math.Min(a.Bounds.Right, b.Bounds.Right);
        return gap <= columnWidth * 0.6;
    }

    private static OcrTextBlock Combine(List<OcrTextBlock> group)
    {
        var ordered = group.OrderByDescending(column => column.Bounds.X).ToList();
        var bounds = ordered.Select(column => column.Bounds).Aggregate(Rect.Union);
        var widths = ordered.Select(column => column.Bounds.Width).OrderBy(width => width).ToList();
        var glyphSize = widths[widths.Count / 2];

        // A lone column still needs to wrap, or the translation is scaled down to fit one line
        // across a strip one glyph wide. Its stack of character cells stands in for the lines it
        // would have had, which both enables wrapping and caps how many lines are reasonable.
        var lines = ordered.Count > 1
            ? ordered.Select(column => column.Bounds).ToList()
            : SplitIntoCharacterCells(bounds, glyphSize);

        return new OcrTextBlock(
            string.Concat(ordered.Select(column => column.Text)),
            bounds,
            lines,
            glyphSize);
    }

    private static List<Rect> SplitIntoCharacterCells(Rect column, double glyphSize)
    {
        int cells = Math.Max(1, (int)Math.Round(column.Height / Math.Max(1, glyphSize)));
        return Enumerable.Range(0, cells)
            .Select(i => new Rect(
                column.X, column.Y + i * column.Height / cells, column.Width, column.Height / cells))
            .ToList();
    }

    /// <summary>
    /// Undoes the quarter turn for one rectangle: a point at (x, y) on the turned page came from
    /// (width - y, x) on the original, so the rectangle's sides swap.
    /// </summary>
    private static Rect MapBack(Rect rotated, int originalWidth) => new(
        originalWidth - (rotated.Y + rotated.Height),
        rotated.X,
        rotated.Height,
        rotated.Width);
}
