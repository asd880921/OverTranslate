namespace OverTranslate;

internal readonly record struct SingleLineOverlayInput(
    double OriginalFontSize,
    double OriginalBorderWidth,
    double TextWidth,
    double HorizontalPadding,
    double RightExpansionWidth,
    double MaxExpandedWidth,
    double ReadableMinFontSize,
    double AbsoluteMinFontSize);

internal readonly record struct SingleLineOverlayResult(
    double FontSize,
    double BorderWidth,
    bool PreferRightExpansion);

internal static class SingleLineOverlayLayout
{
    public static SingleLineOverlayResult Calculate(SingleLineOverlayInput input)
    {
        var idealBorderWidth = Math.Max(input.OriginalBorderWidth, input.TextWidth + input.HorizontalPadding);
        if (idealBorderWidth <= input.RightExpansionWidth)
            return new(input.OriginalFontSize, idealBorderWidth, true);

        var originalInnerWidth = Math.Max(1, input.OriginalBorderWidth - input.HorizontalPadding);
        var scaledToOriginalWidth = ScaleFont(input.OriginalFontSize, originalInnerWidth, input.TextWidth);
        if (scaledToOriginalWidth >= input.ReadableMinFontSize)
            return new(scaledToOriginalWidth, input.OriginalBorderWidth, false);

        var readableWidth = WidthAtFontSize(
            input.TextWidth,
            input.OriginalFontSize,
            input.ReadableMinFontSize,
            input.HorizontalPadding);
        var expandedBorderWidth = Math.Min(
            input.MaxExpandedWidth,
            Math.Max(input.OriginalBorderWidth, readableWidth));
        var expandedInnerWidth = Math.Max(1, expandedBorderWidth - input.HorizontalPadding);
        var scaledAfterExpansion = ScaleFont(input.OriginalFontSize, expandedInnerWidth, input.TextWidth);

        return new(
            Math.Max(input.AbsoluteMinFontSize, Math.Min(input.ReadableMinFontSize, scaledAfterExpansion)),
            expandedBorderWidth,
            false);
    }

    private static double ScaleFont(double fontSize, double targetWidth, double measuredWidth) =>
        measuredWidth <= 0 ? fontSize : fontSize * targetWidth / measuredWidth;

    private static double WidthAtFontSize(
        double measuredWidth,
        double measuredFontSize,
        double targetFontSize,
        double horizontalPadding) =>
        measuredFontSize <= 0
            ? horizontalPadding
            : measuredWidth * targetFontSize / measuredFontSize + horizontalPadding;
}
