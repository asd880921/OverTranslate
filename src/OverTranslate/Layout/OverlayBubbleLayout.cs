using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings, so these
// names collide with their WPF counterparts
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

namespace OverTranslate.Layout;

/// <summary>
/// Where one translated bubble ends up and how its text is drawn, in canvas (DIP) coordinates.
/// Pure data: no visual tree, so the whole layout can be exercised in tests.
/// </summary>
public sealed record OverlayBubble(
    string Text,
    double Left,
    double Top,
    double Width,
    double Height,
    double FontSize,
    bool Wrap,
    Color Background,
    Color Foreground,
    /// <summary>Set when the text is stacked downwards in columns running right to left.</summary>
    bool Vertical = false,
    /// <summary>Side of one character's cell. Only meaningful when <see cref="Vertical"/>.</summary>
    double CellSize = 0);

/// <summary>Everything the layout needs to know about the surface it is drawing onto.</summary>
/// <param name="OriginPhysX">
/// Physical-pixel X of the origin that block bounds are relative to — the capture selection on
/// screen, or the region's offset within a source image in batch export.
/// </param>
/// <param name="SurfacePhysLeft">
/// Physical-pixel X of the canvas's own left edge. The screen overlay spans the whole virtual
/// desktop and so may be negative; an exported image starts at zero.
/// </param>
public sealed record OverlayLayoutContext(
    double DpiX,
    double DpiY,
    double OriginPhysX,
    double OriginPhysY,
    double OriginPhysWidth,
    double OriginPhysHeight,
    double SurfacePhysLeft,
    double SurfacePhysTop,
    double CanvasWidth,
    double CanvasHeight,
    string SourceLanguage,
    string TargetLanguage,
    /// <summary>
    /// How far a bubble may grow sideways, as a multiple of the text it covers. Unlimited by
    /// default, which is what the screen overlay wants: there the selection is already drawn
    /// tightly around the text, so the selection's own edge is the real limit. Exporting a whole
    /// page has no such edge, and an unbounded bubble stretches a short line clear across the
    /// artwork, so that path sets a bound.
    /// </summary>
    double MaxWidthFactor = double.PositiveInfinity,
    /// <summary>
    /// Lay the translation out downwards, as the source was. Keeping the direction means the
    /// bubble needs no more room than the text it replaces, so nothing spills over the artwork,
    /// and the page still reads the way it was drawn to.
    /// </summary>
    bool VerticalText = false);

/// <summary>
/// Places translation bubbles over the text they cover, sizing each one to stay readable without
/// spilling onto its neighbours. Extracted from OverlayWindow so the on-screen overlay and the
/// batch image export lay text out identically — two implementations would drift, and the batch
/// output is meant to look exactly like what the live overlay shows.
/// </summary>
public static class OverlayBubbleLayout
{
    private const double OverlayPadding = 6;
    private const double BubbleExpand = 2;
    private const double BubbleMinWidth = 30;
    private const double BubbleHorizontalPadding = 6;
    private const double BubbleVerticalPadding = 6;
    private const double DefaultMinFontSize = 13.0;
    private const double SmallTextMinFontSize = 14.5;
    private const double SingleLineAbsoluteMinFontSize = 9.5;
    private const double SingleLineReadableMinFontSize = 10.0;
    private const double SingleLineEmergencyMinFontSize = 7.0;
    private const double WrappedAbsoluteMinFontSize = 11.0;
    private const double GroupedEmergencyMinFontSize = 7.0;

    public const string FontFamilyName = "Microsoft JhengHei, Segoe UI, Sans-Serif";

    public static Typeface CreateTypeface() => new(
        new FontFamily(FontFamilyName), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static IReadOnlyList<OverlayBubble> Calculate(
        IReadOnlyList<TranslatedBlock> blocks, OverlayLayoutContext context)
    {
        var bubbles = new List<OverlayBubble>();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;
            bubbles.Add(CalculateOne(block, blocks, context));
        }

        return bubbles;
    }

    private static OverlayBubble CalculateOne(
        TranslatedBlock block, IReadOnlyList<TranslatedBlock> blocks, OverlayLayoutContext ctx)
    {
        // Block bounds are relative to the origin; place them on the canvas in DIPs.
        double physX = ctx.OriginPhysX + block.Bounds.X;
        double physY = ctx.OriginPhysY + block.Bounds.Y;

        double canvasX = (physX - ctx.SurfacePhysLeft) / ctx.DpiX;
        double canvasY = (physY - ctx.SurfacePhysTop) / ctx.DpiY;
        double wpfW = block.Bounds.Width / ctx.DpiX;
        double wpfH = block.Bounds.Height / ctx.DpiY;
        double sourceFontReferenceHeight = GetSourceFontReferenceHeight(block, wpfH, ctx);

        // Expand coverage 2px beyond OCR bounds on every side to eliminate edge bleed
        double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
        double borderH = wpfH + BubbleExpand * 2;

        var bg = block.BackgroundColor.A == 0 ? Colors.White : block.BackgroundColor;

        Color textColor;
        if (block.TextColor.A != 0)
        {
            textColor = block.TextColor;
        }
        else
        {
            double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            textColor = lum > 0.5 ? Colors.Black : Colors.White;
        }

        if (ctx.VerticalText)
            return CalculateVertical(block, canvasX, canvasY, wpfW, wpfH, sourceFontReferenceHeight, bg, textColor, ctx);

        bool isSmallSourceText = sourceFontReferenceHeight <= 14;
        bool isSingleLineSource = IsSingleLineSource(block.OriginalText, sourceFontReferenceHeight);
        bool isGroupedMultiLineSource = block.SourceLineBounds is { Count: > 1 };
        double minFontSize = isSmallSourceText ? SmallTextMinFontSize : DefaultMinFontSize;
        double fontSize = Math.Max(minFontSize, sourceFontReferenceHeight * (isSmallSourceText ? 1.18 : 1.06));
        if (ShouldSlightlyBoostTinyEnglishToCjk(sourceFontReferenceHeight, ctx))
            fontSize = Math.Min(fontSize * 1.08, fontSize + 1.25);

        var typeface = CreateTypeface();
        double widthCeiling = double.IsPositiveInfinity(ctx.MaxWidthFactor)
            ? double.PositiveInfinity
            : Math.Max(BubbleMinWidth, borderW * ctx.MaxWidthFactor);
        double availableWidth = Math.Min(
            widthCeiling, Math.Max(BubbleMinWidth, ctx.CanvasWidth - OverlayPadding * 2));
        double targetBorderW = borderW;
        bool preferRightExpansion = false;
        bool wrap = false;
        double? maxWrapBorderHeight = null;

        var measured = MeasureText(block.TranslatedText, typeface, fontSize, ctx);
        double innerW = Math.Max(1, borderW - BubbleHorizontalPadding);

        if (isGroupedMultiLineSource)
        {
            wrap = true;
            var sourceLineCount = block.SourceLineBounds!.Count;
            var hasLowerBlock = HasLowerOverlappingBlock(block, blocks);
            var maxLineCount = hasLowerBlock ? sourceLineCount : sourceLineCount + 1;
            var rightAvailableW = Math.Min(
                widthCeiling, GetRightExpansionWidth(block, blocks, canvasX, canvasY, wpfH, ctx));
            var preferredGroupedWidth = Math.Min(
                availableWidth,
                Math.Max(borderW, Math.Min(measured.Width + BubbleHorizontalPadding, rightAvailableW)));
            targetBorderW = preferredGroupedWidth;
            preferRightExpansion = targetBorderW > borderW;
            var maxBorderHeight = GetBottomAvailableHeight(block, blocks, canvasX, canvasY, wpfW, ctx);
            maxWrapBorderHeight = maxBorderHeight;
            fontSize = FindLargestGroupedFontSize(
                block.TranslatedText,
                typeface,
                fontSize,
                SingleLineAbsoluteMinFontSize,
                Math.Max(1, targetBorderW - BubbleHorizontalPadding),
                maxLineCount,
                maxBorderHeight,
                ctx);
        }
        else if (isSingleLineSource)
        {
            double rightAvailableW = Math.Min(
                widthCeiling, GetRightExpansionWidth(block, blocks, canvasX, canvasY, wpfH, ctx));
            var layout = SingleLineOverlayLayout.Calculate(new(
                fontSize,
                borderW,
                measured.Width,
                BubbleHorizontalPadding,
                rightAvailableW,
                availableWidth,
                SingleLineReadableMinFontSize,
                SingleLineAbsoluteMinFontSize));

            fontSize = layout.FontSize;
            targetBorderW = layout.BorderWidth;
            preferRightExpansion = layout.PreferRightExpansion;

            var finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize, ctx);
            var singleLineInnerWidth = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
            var stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
            if (stillOverflowsSingleLine)
            {
                var fittedFontSize = fontSize * singleLineInnerWidth / finalSingleLineMeasure.Width;
                if (fittedFontSize >= SingleLineEmergencyMinFontSize)
                {
                    fontSize = fittedFontSize;
                    finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize, ctx);
                    stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                }
                else if (fontSize > SingleLineEmergencyMinFontSize)
                {
                    fontSize = SingleLineEmergencyMinFontSize;
                    finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize, ctx);
                    stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                }
            }

            if (stillOverflowsSingleLine && !HasLowerOverlappingBlock(block, blocks))
            {
                var maxBorderHeight = GetBottomAvailableHeight(block, blocks, canvasX, canvasY, wpfW, ctx);
                var wrappedLineCount = EstimateWrappedLineCount(
                    block.TranslatedText, typeface, fontSize, singleLineInnerWidth, ctx);
                var wrappedMeasure = MeasureText(
                    block.TranslatedText, typeface, fontSize, ctx, singleLineInnerWidth);
                var wrappedBorderHeight = wrappedMeasure.Height + BubbleVerticalPadding;

                if (wrappedLineCount <= 2 && wrappedBorderHeight <= maxBorderHeight)
                {
                    wrap = true;
                    maxWrapBorderHeight = maxBorderHeight;
                }
            }
        }
        else
        {
            if (measured.Width > innerW)
            {
                double scaledFont = fontSize * innerW / measured.Width;
                if (scaledFont >= minFontSize)
                {
                    fontSize = scaledFont;
                }
                else
                {
                    fontSize = Math.Max(WrappedAbsoluteMinFontSize, minFontSize);
                    wrap = true;
                }
            }
        }

        double actualBorderH = Math.Max(borderH, fontSize + BubbleVerticalPadding);
        if (wrap)
        {
            innerW = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
            var wrapMeasured = MeasureText(block.TranslatedText, typeface, fontSize, ctx, innerW);
            actualBorderH = Math.Max(actualBorderH, wrapMeasured.Height + BubbleVerticalPadding);
            if (maxWrapBorderHeight.HasValue)
                // Cap growth so a longer translation does not spill over the block below — but
                // never below borderH (the source's own height). When the next block sits right
                // under a multi-line source, maxWrapBorderHeight is slightly shorter than the
                // source, and clamping to it left the last source line uncovered (original text
                // bleeding through). Covering one's own source wins over not touching a neighbour;
                // that neighbour's own opaque bubble covers it.
                actualBorderH = Math.Min(actualBorderH, Math.Max(maxWrapBorderHeight.Value, borderH));
        }

        double expandedOffsetX = (targetBorderW - borderW) / 2;
        double left = preferRightExpansion
            ? Math.Clamp(canvasX - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, ctx.CanvasWidth - targetBorderW - OverlayPadding))
            : Math.Clamp(canvasX - BubbleExpand - expandedOffsetX, OverlayPadding, Math.Max(OverlayPadding, ctx.CanvasWidth - targetBorderW - OverlayPadding));
        double top = Math.Clamp(canvasY - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, ctx.CanvasHeight - actualBorderH - OverlayPadding));

        return new OverlayBubble(
            block.TranslatedText, left, top, targetBorderW, actualBorderH, fontSize, wrap, bg, textColor);
    }

    /// <summary>
    /// Fills the source's own footprint with a grid of character cells, columns running right to
    /// left. The cell starts at the size the original glyphs were and shrinks only as far as it
    /// must for the translation to fit, so the replacement sits where the original did.
    /// </summary>
    private static OverlayBubble CalculateVertical(
        TranslatedBlock block,
        double canvasX,
        double canvasY,
        double wpfW,
        double wpfH,
        double sourceGlyphSize,
        Color background,
        Color foreground,
        OverlayLayoutContext ctx)
    {
        double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
        double borderH = wpfH + BubbleExpand * 2;

        // Line breaks belong to the source's own wrapping; the grid does its own.
        var text = new string(block.TranslatedText.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int needed = Math.Max(1, text.Length);

        double cell = Math.Max(SingleLineAbsoluteMinFontSize, sourceGlyphSize);
        while (cell > SingleLineEmergencyMinFontSize && Capacity(borderW, borderH, cell) < needed)
            cell -= 0.5;

        // Still short of room at the smallest readable cell: let the grid overflow downwards rather
        // than drop characters, since a truncated line is worse than a slightly tall bubble.
        double columns = Math.Max(1, Math.Floor(borderW / cell));
        double rows = Math.Max(1, Math.Ceiling(needed / columns));
        borderH = Math.Max(borderH, rows * cell);

        double left = Math.Clamp(
            canvasX - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, ctx.CanvasWidth - borderW - OverlayPadding));
        double top = Math.Clamp(
            canvasY - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, ctx.CanvasHeight - borderH - OverlayPadding));

        return new OverlayBubble(
            text, left, top, borderW, borderH,
            FontSize: cell * 0.92,   // a little air around each glyph, as the source has
            Wrap: false,
            background, foreground,
            Vertical: true,
            CellSize: cell);
    }

    private static int Capacity(double width, double height, double cell) =>
        (int)Math.Max(0, Math.Floor(width / cell)) * (int)Math.Max(0, Math.Floor(height / cell));

    /// <summary>Cell each character occupies, in reading order: down a column, then leftwards.</summary>
    public static IEnumerable<(char Glyph, Rect Cell)> VerticalCells(OverlayBubble bubble)
    {
        double cell = bubble.CellSize > 0 ? bubble.CellSize : bubble.FontSize;
        int columns = Math.Max(1, (int)Math.Floor(bubble.Width / cell));
        int rows = Math.Max(1, (int)Math.Floor(bubble.Height / cell));

        for (int i = 0; i < bubble.Text.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;
            if (column >= columns) yield break;

            yield return (bubble.Text[i], new Rect(
                bubble.Left + bubble.Width - (column + 1) * cell,
                bubble.Top + row * cell,
                cell,
                cell));
        }
    }

    private static bool IsSingleLineSource(string originalText, double sourceHeight) =>
        !originalText.Contains('\n') &&
        !originalText.Contains('\r') &&
        sourceHeight <= 28;

    private static double GetSourceFontReferenceHeight(
        TranslatedBlock block, double fallbackHeight, OverlayLayoutContext ctx)
    {
        // Latin blocks carry the reduced glyph height separately so the font is not sized from
        // the (much taller) full coverage box. This takes priority over the line-bounds median.
        if (block.SourceGlyphHeight is { } glyphHeight && glyphHeight > 0)
            return glyphHeight / ctx.DpiY;

        if (block.SourceLineBounds is not { Count: > 0 })
            return fallbackHeight;

        var lineHeights = block.SourceLineBounds
            .Select(bounds => bounds.Height / ctx.DpiY)
            .OrderBy(height => height)
            .ToList();
        return lineHeights[lineHeights.Count / 2];
    }

    private static bool ShouldSlightlyBoostTinyEnglishToCjk(
        double sourceFontReferenceHeight, OverlayLayoutContext ctx) =>
        sourceFontReferenceHeight <= 14 &&
        ctx.SourceLanguage.Equals("EN", StringComparison.OrdinalIgnoreCase) &&
        IsCjkLanguage(ctx.TargetLanguage);

    private static bool IsCjkLanguage(string language) =>
        language.Equals("ZH", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ZH-", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("JA", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("KO", StringComparison.OrdinalIgnoreCase);

    private static double FindLargestGroupedFontSize(
        string text,
        Typeface typeface,
        double preferredFontSize,
        double minimumFontSize,
        double maxTextWidth,
        int maxLineCount,
        double maxBorderHeight,
        OverlayLayoutContext ctx)
    {
        for (double size = preferredFontSize; size >= minimumFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, ctx, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth, ctx) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        for (double size = minimumFontSize - 0.5; size >= GroupedEmergencyMinFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, ctx, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth, ctx) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        return GroupedEmergencyMinFontSize;
    }

    private static int EstimateWrappedLineCount(
        string text, Typeface typeface, double fontSize, double maxTextWidth, OverlayLayoutContext ctx)
    {
        var singleLine = MeasureText(text, typeface, fontSize, ctx);
        if (singleLine.Width <= maxTextWidth)
            return 1;

        var wrapped = MeasureText(text, typeface, fontSize, ctx, maxTextWidth);
        var lineHeight = Math.Max(1, MeasureText("Ag", typeface, fontSize, ctx).Height);
        return Math.Max(1, (int)Math.Ceiling((wrapped.Height - 0.1) / lineHeight));
    }

    private static double GetBottomAvailableHeight(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfW,
        OverlayLayoutContext ctx)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentRight = currentLeft + wpfW + BubbleExpand * 2;
        double selectionTop = (ctx.OriginPhysY - ctx.SurfacePhysTop) / ctx.DpiY;
        double selectionBottom = selectionTop + ctx.OriginPhysHeight / ctx.DpiY;
        double bottomLimit = Math.Min(ctx.CanvasHeight - OverlayPadding, selectionBottom);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.Y <= current.Bounds.Y)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / ctx.DpiX - BubbleExpand;
            double otherRight = otherLeft + other.Bounds.Width / ctx.DpiX + BubbleExpand * 2;
            bool overlapsHorizontally = otherLeft < currentRight && otherRight > currentLeft;
            if (!overlapsHorizontally)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / ctx.DpiY - OverlayPadding;
            bottomLimit = Math.Min(bottomLimit, otherTop);
        }

        return Math.Max(0, bottomLimit - (canvasY - BubbleExpand));
    }

    private static bool HasLowerOverlappingBlock(
        TranslatedBlock current, IReadOnlyList<TranslatedBlock> blocks)
    {
        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.Y <= current.Bounds.Y)
                continue;

            var overlapsHorizontally =
                other.Bounds.Left < current.Bounds.Right &&
                other.Bounds.Right > current.Bounds.Left;
            if (overlapsHorizontally)
                return true;
        }

        return false;
    }

    private static double GetRightExpansionWidth(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfH,
        OverlayLayoutContext ctx)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentTop = canvasY - BubbleExpand;
        double currentBottom = currentTop + wpfH + BubbleExpand * 2;
        double selectionLeft = (ctx.OriginPhysX - ctx.SurfacePhysLeft) / ctx.DpiX;
        double selectionRight = selectionLeft + ctx.OriginPhysWidth / ctx.DpiX;
        double rightLimit = Math.Min(ctx.CanvasWidth - OverlayPadding, selectionRight);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.X <= current.Bounds.X)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / ctx.DpiY - BubbleExpand;
            double otherBottom = otherTop + other.Bounds.Height / ctx.DpiY + BubbleExpand * 2;
            bool overlapsVertically = otherTop < currentBottom && otherBottom > currentTop;
            if (!overlapsVertically)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / ctx.DpiX - BubbleExpand;
            rightLimit = Math.Min(rightLimit, otherLeft - OverlayPadding);
        }

        return Math.Max(0, rightLimit - currentLeft);
    }

    private static FormattedText MeasureText(
        string text, Typeface typeface, double fontSize, OverlayLayoutContext ctx, double? maxTextWidth = null)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            ctx.DpiY);

        if (maxTextWidth.HasValue)
            formattedText.MaxTextWidth = maxTextWidth.Value;

        return formattedText;
    }
}
