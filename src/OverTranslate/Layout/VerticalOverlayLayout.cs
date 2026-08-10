using System.Buffers;
using System.Windows;
using OverTranslate.Services;

namespace OverTranslate.Layout;

internal readonly record struct VerticalOverlayInput(
    string Text,
    double SourceLeft,
    double SourceTop,
    double SourceWidth,
    double SourceHeight,
    double SourceGlyphSize,
    double CanvasWidth,
    double CanvasHeight);

internal sealed record VerticalOverlayResult(
    string Text,
    double Left,
    double Top,
    double Width,
    double Height,
    double FontSize,
    double CellSize);

/// <summary>
/// Restores translated vertical text to the source footprint: characters run downwards, then the
/// next column starts on the left. Ported from the retired batch-image renderer, but selected per
/// OCR block so one screenshot can contain horizontal and vertical text at the same time.
/// </summary>
internal static class VerticalOverlayLayout
{
    private const double OverlayPadding = 6;
    private const double BubbleExpand = 2;
    private const double BubbleMinWidth = 30;
    private const double PreferredMinCellSize = 9.5;
    private const double EmergencyMinCellSize = 7.0;
    private const double VerticalAspectRatio = 1.5;

    private static readonly SearchValues<char> RotatedGlyphs = SearchValues.Create(
        // Brackets and quotation marks — their opening side has to face up in vertical text.
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        // Dashes, rules and the prolonged sound mark must become vertical strokes.
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        // Ellipses and leaders must become vertical rows of dots.
        "…⋯‥");

    public static bool IsVerticalSource(TranslatedBlock block)
    {
        if (!IsVerticalBounds(block.Bounds))
            return false;

        // A narrow horizontal paragraph can have a tall union box. Its individual source lines
        // remain wide, unlike lines mapped back from the rotated OCR pass, so do not misclassify it.
        return block.SourceLineBounds is not { Count: > 0 } lines ||
               lines.All(IsVerticalBounds);
    }

    public static VerticalOverlayResult Calculate(VerticalOverlayInput input)
    {
        // Line breaks belong to horizontal source grouping; the vertical grid performs its own
        // wrapping into columns.
        var text = new string(input.Text.Where(character => !char.IsWhiteSpace(character)).ToArray());
        var needed = Math.Max(1, text.Length);
        var width = Math.Max(input.SourceWidth + BubbleExpand * 2, BubbleMinWidth);
        var height = input.SourceHeight + BubbleExpand * 2;
        var cellSize = Math.Max(PreferredMinCellSize, input.SourceGlyphSize);

        while (cellSize > EmergencyMinCellSize && Capacity(width, height, cellSize) < needed)
            cellSize -= 0.5;

        // Never truncate a long translation. If the source footprint cannot hold every character
        // at the emergency size, extend downwards while preserving its original narrow width.
        var columns = Math.Max(1, Math.Floor(width / cellSize));
        var rows = Math.Max(1, Math.Ceiling(needed / columns));
        height = Math.Max(height, rows * cellSize);

        var left = Math.Clamp(
            input.SourceLeft - BubbleExpand,
            OverlayPadding,
            Math.Max(OverlayPadding, input.CanvasWidth - width - OverlayPadding));
        var top = Math.Clamp(
            input.SourceTop - BubbleExpand,
            OverlayPadding,
            Math.Max(OverlayPadding, input.CanvasHeight - height - OverlayPadding));

        return new VerticalOverlayResult(
            text,
            left,
            top,
            width,
            height,
            FontSize: cellSize * 0.92,
            CellSize: cellSize);
    }

    /// <summary>Cells in reading order: down the rightmost column, then leftwards.</summary>
    public static IEnumerable<(char Glyph, Rect Cell)> Cells(VerticalOverlayResult layout)
    {
        var columns = Math.Max(1, (int)Math.Floor(layout.Width / layout.CellSize));
        var rows = Math.Max(1, (int)Math.Floor(layout.Height / layout.CellSize));

        for (var index = 0; index < layout.Text.Length; index++)
        {
            var column = index / rows;
            var row = index % rows;
            if (column >= columns)
                yield break;

            yield return (layout.Text[index], new Rect(
                layout.Left + layout.Width - (column + 1) * layout.CellSize,
                layout.Top + row * layout.CellSize,
                layout.CellSize,
                layout.CellSize));
        }
    }

    public static bool RotatesInVerticalText(char glyph) => RotatedGlyphs.Contains(glyph);

    private static bool IsVerticalBounds(Rect bounds) =>
        bounds.Width > 0 &&
        bounds.Height >= 24 &&
        bounds.Height >= bounds.Width * VerticalAspectRatio;

    private static int Capacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));
}
