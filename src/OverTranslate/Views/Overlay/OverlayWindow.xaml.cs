using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Layout;

namespace OverTranslate.Views.Overlay;

public partial class OverlayWindow : Window
{
    private const double OverlayPadding = 6;
    private const double BubbleExpand = 2;
    private const double BubbleMinWidth = 30;
    private const double BubbleHorizontalPadding = 6;
    private const double BubbleVerticalPadding = 6;
    private const double SingleLineAbsoluteMinFontSize = 9.5;
    private const double SingleLineReadableMinFontSize = 10.0;
    private const double SingleLineEmergencyMinFontSize = 7.0;
    private const double WrappedAbsoluteMinFontSize = 11.0;
    private const double GroupedEmergencyMinFontSize = 7.0;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    // Pixel rect this window covers. Left/Top/Width/Height cannot stand in for it: they are DIP
    // scaled by one monitor's DPI, which on a mixed-DPI desktop is not the rect the OCR coordinates
    // (physical pixels) are measured in.
    private readonly System.Drawing.Rectangle _physBounds = ScreenGeometry.VirtualDesktopBounds();

    private bool _isLoaded;
    private List<TranslatedBlock> _currentBlocks;
    private double _currentSelectionScreenX;
    private double _currentSelectionScreenY;
    private double _currentSelectionScreenWidth;
    private double _currentSelectionScreenHeight;
    private string _currentSourceLanguage;
    private string _currentTargetLanguage;

    public OverlayWindow(
        List<TranslatedBlock> blocks,
        double selectionScreenX,
        double selectionScreenY,
        double selectionScreenWidth,
        double selectionScreenHeight,
        string sourceLanguage,
        string targetLanguage)
    {
        InitializeComponent();
        _currentBlocks = blocks;
        _currentSelectionScreenX = selectionScreenX;
        _currentSelectionScreenY = selectionScreenY;
        _currentSelectionScreenWidth = selectionScreenWidth;
        _currentSelectionScreenHeight = selectionScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;

        // Provisional: OnSourceInitialized pins the window to _physBounds instead.
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) =>
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                _dpiX = src.CompositionTarget.TransformToDevice.M11;
                _dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            _isLoaded = true;
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowStyles.ApplyClickThrough(this);

        // Before Loaded reads the DPI: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);
    }

    // Shows a centered status card and clears old bubbles so the indicator is unobstructed.
    public void ShowProcessing(double selPhysX, double selPhysY, double selPhysW, double selPhysH, string statusText)
    {
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();

        double winPhysLeft = _physBounds.Left;
        double winPhysTop  = _physBounds.Top;

        ProcessingText.Text = statusText;
        ProcessingBorder.Visibility = Visibility.Hidden;

        // This window spans every monitor and so renders at a single DPI; on a monitor at another
        // scale the card would come out the wrong physical size. Applied before Measure so the
        // desired size below is the transformed one the centring needs.
        double relScale = ScreenGeometry.ScaleAt(
            (int)(selPhysX + selPhysW / 2), (int)(selPhysY + selPhysH / 2)) / _dpiX;
        ProcessingBorder.LayoutTransform = relScale == 1.0
            ? Transform.Identity
            : new ScaleTransform(relScale, relScale);

        ProcessingBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ProcessingBorder.DesiredSize;
        double cx = (selPhysX + selPhysW / 2 - winPhysLeft) / _dpiX - desired.Width  / 2;
        double cy = (selPhysY + selPhysH / 2 - winPhysTop)  / _dpiY - desired.Height / 2;
        Canvas.SetLeft(ProcessingBorder, cx);
        Canvas.SetTop(ProcessingBorder,  cy);
        ProcessingBorder.Visibility = Visibility.Visible;
    }

    public void UpdateBlocks(
        List<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight,
        string sourceLanguage,
        string targetLanguage)
    {
        _currentBlocks = blocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        _currentSelectionScreenWidth = selScreenWidth;
        _currentSelectionScreenHeight = selScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        ProcessingBorder.Visibility = Visibility.Collapsed;
        SetTranslationLayersVisible(true);
        if (_isLoaded)
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
    }

    public void RestoreIdle(bool hasVisibleBlocks)
    {
        ProcessingBorder.Visibility = Visibility.Collapsed;
        if (hasVisibleBlocks && _isLoaded && BubbleBackgroundCanvas.Children.Count == 0 && _currentBlocks.Count > 0)
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
        SetTranslationLayersVisible(hasVisibleBlocks);
    }

    public void SetBubblesVisible(bool visible) => SetTranslationLayersVisible(visible);

    // Renders the translation bubble layers cropped to the given selection region (physical pixels)
    // as a transparent overlay image, for the "copy screenshot" feature. The loading indicator is
    // never included: while processing the bubble canvases are cleared, so the guard below returns
    // null and only the clean original is copied. Returns null when nothing is currently shown
    // (pre-translation, processing, or toggled to original).
    public System.Windows.Media.Imaging.BitmapSource? RenderBubblesForSelection(
        double selPhysLeft, double selPhysTop, int selPhysWidth, int selPhysHeight)
    {
        if (!_isLoaded) return null;
        if (BubbleBackgroundCanvas.Visibility != Visibility.Visible) return null;
        if (BubbleBackgroundCanvas.Children.Count == 0 && BubbleTextCanvas.Children.Count == 0)
            return null;

        int fullW = Math.Max(1, _physBounds.Width);
        int fullH = Math.Max(1, _physBounds.Height);

        // Render the whole overlay content (both bubble layers) at physical resolution. The
        // processing indicator is Collapsed whenever bubbles exist, so it does not appear.
        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
            fullW, fullH, 96 * _dpiX, 96 * _dpiY, System.Windows.Media.PixelFormats.Pbgra32);
        full.Render((Visual)Content);

        // The overlay window spans the whole virtual screen; the selection sits at this physical
        // offset within it.
        int cropX = Math.Clamp((int)Math.Round(selPhysLeft - _physBounds.Left), 0, fullW - 1);
        int cropY = Math.Clamp((int)Math.Round(selPhysTop  - _physBounds.Top),  0, fullH - 1);
        int cropW = Math.Clamp(selPhysWidth,  1, fullW - cropX);
        int cropH = Math.Clamp(selPhysHeight, 1, fullH - cropY);

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
            full, new Int32Rect(cropX, cropY, cropW, cropH));
        cropped.Freeze();
        return cropped;
    }

    private void BuildOverlay(
        List<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight)
    {
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();

        // Window top-left in physical pixels
        double winPhysLeft = _physBounds.Left;
        double winPhysTop = _physBounds.Top;
        double canvasWidth = BubbleBackgroundCanvas.ActualWidth > 0 ? BubbleBackgroundCanvas.ActualWidth : Width;
        double canvasHeight = BubbleBackgroundCanvas.ActualHeight > 0 ? BubbleBackgroundCanvas.ActualHeight : Height;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            // Physical pixel position on screen
            double physX = selScreenX + block.Bounds.X;
            double physY = selScreenY + block.Bounds.Y;
            double physW = block.Bounds.Width;
            double physH = block.Bounds.Height;

            // Convert to WPF canvas coords (relative to overlay window)
            double canvasX = (physX - winPhysLeft) / _dpiX;
            double canvasY = (physY - winPhysTop) / _dpiY;
            double wpfW = physW / _dpiX;
            double wpfH = physH / _dpiY;
            double sourceFontReferenceHeight = GetSourceFontReferenceHeight(block, wpfH);

            // Expand coverage 2px beyond OCR bounds on every side to eliminate edge bleed
            double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
            double borderH = wpfH + BubbleExpand * 2;

            var bg = block.BackgroundColor.A == 0
                ? Colors.White
                : block.BackgroundColor;

            System.Windows.Media.Brush textBrush;
            if (block.TextColor.A != 0)
                textBrush = new SolidColorBrush(block.TextColor);
            else
            {
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                textBrush = lum > 0.5 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            }

            bool isSingleLineSource = IsSingleLineSource(block.OriginalText, sourceFontReferenceHeight);
            bool isGroupedMultiLineSource = block.SourceLineBounds is { Count: > 1 };
            double minFontSize = SourceFontScale.MinFontSize(sourceFontReferenceHeight);
            double fontSize = SourceFontScale.Calculate(sourceFontReferenceHeight, IsLatinSourceToCjkTarget());
            var typeface = new Typeface(
                new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double availableWidth = Math.Max(BubbleMinWidth, canvasWidth - OverlayPadding * 2);
            double targetBorderW = borderW;
            bool preferRightExpansion = false;
            bool wrap = false;
            double? maxWrapBorderHeight = null;

            var measured = MeasureText(block.TranslatedText, typeface, fontSize);
            double innerW = Math.Max(1, borderW - BubbleHorizontalPadding);
            if (isGroupedMultiLineSource)
            {
                wrap = true;
                var sourceLineCount = block.SourceLineBounds!.Count;
                var hasLowerBlock = HasLowerOverlappingBlock(block, blocks);
                var maxLineCount = hasLowerBlock ? sourceLineCount : sourceLineCount + 1;
                var rightAvailableW = GetRightExpansionWidth(
                    block,
                    blocks,
                    canvasX,
                    canvasY,
                    wpfH,
                    selScreenX,
                    selScreenWidth,
                    canvasWidth);
                var preferredGroupedWidth = Math.Min(
                    availableWidth,
                    Math.Max(borderW, Math.Min(measured.Width + BubbleHorizontalPadding, rightAvailableW)));
                targetBorderW = preferredGroupedWidth;
                preferRightExpansion = targetBorderW > borderW;
                var maxBorderHeight = GetBottomAvailableHeight(
                    block,
                    blocks,
                    canvasX,
                    canvasY,
                    wpfW,
                    selScreenY,
                    selScreenHeight,
                    canvasHeight);
                maxWrapBorderHeight = maxBorderHeight;
                fontSize = FindLargestGroupedFontSize(
                    block.TranslatedText,
                    typeface,
                    fontSize,
                    SingleLineAbsoluteMinFontSize,
                    Math.Max(1, targetBorderW - BubbleHorizontalPadding),
                    maxLineCount,
                    maxBorderHeight);
            }
            else if (isSingleLineSource)
            {
                double rightAvailableW = GetRightExpansionWidth(
                    block,
                    blocks,
                    canvasX,
                    canvasY,
                    wpfH,
                    selScreenX,
                    selScreenWidth,
                    canvasWidth);
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

                var finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                var singleLineInnerWidth = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
                var stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                if (stillOverflowsSingleLine)
                {
                    var fittedFontSize = fontSize * singleLineInnerWidth / finalSingleLineMeasure.Width;
                    if (fittedFontSize >= SingleLineEmergencyMinFontSize)
                    {
                        fontSize = fittedFontSize;
                        finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                        stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                    }
                    else if (fontSize > SingleLineEmergencyMinFontSize)
                    {
                        fontSize = SingleLineEmergencyMinFontSize;
                        finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                        stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                    }
                }

                if (stillOverflowsSingleLine && !HasLowerOverlappingBlock(block, blocks))
                {
                    var maxBorderHeight = GetBottomAvailableHeight(
                        block,
                        blocks,
                        canvasX,
                        canvasY,
                        wpfW,
                        selScreenY,
                        selScreenHeight,
                        canvasHeight);
                    var wrappedLineCount = EstimateWrappedLineCount(
                        block.TranslatedText,
                        typeface,
                        fontSize,
                        singleLineInnerWidth);
                    var wrappedMeasure = MeasureText(
                        block.TranslatedText,
                        typeface,
                        fontSize,
                        singleLineInnerWidth);
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
                var wrapMeasured = MeasureText(block.TranslatedText, typeface, fontSize, innerW);
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

            var backgroundBorder = new Border
            {
                Background = new SolidColorBrush(bg),
                Padding = new Thickness(3, 2, 3, 2),
                Width  = targetBorderW,
                Height = actualBorderH,
                ClipToBounds = true,
            };

            var textContainer = new Border
            {
                Padding = new Thickness(3, 2, 3, 2),
                Width = targetBorderW,
                Height = actualBorderH,
                ClipToBounds = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = block.TranslatedText,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textBrush,
                    TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                }
            };

            double expandedOffsetX = (targetBorderW - borderW) / 2;
            double left = preferRightExpansion
                ? Math.Clamp(canvasX - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, canvasWidth - targetBorderW - OverlayPadding))
                : Math.Clamp(canvasX - BubbleExpand - expandedOffsetX, OverlayPadding, Math.Max(OverlayPadding, canvasWidth - targetBorderW - OverlayPadding));
            double top = Math.Clamp(canvasY - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, canvasHeight - actualBorderH - OverlayPadding));
            Canvas.SetLeft(backgroundBorder, left);
            Canvas.SetTop(backgroundBorder, top);
            Canvas.SetLeft(textContainer, left);
            Canvas.SetTop(textContainer, top);
            BubbleBackgroundCanvas.Children.Add(backgroundBorder);
            BubbleTextCanvas.Children.Add(textContainer);
        }
    }

    private void SetTranslationLayersVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BubbleBackgroundCanvas.Visibility = visibility;
        BubbleTextCanvas.Visibility = visibility;
    }

    private static bool IsSingleLineSource(string originalText, double sourceHeight) =>
        !originalText.Contains('\n') &&
        !originalText.Contains('\r') &&
        sourceHeight <= 28;

    private double GetSourceFontReferenceHeight(TranslatedBlock block, double fallbackHeight)
    {
        // Latin blocks carry the reduced glyph height separately so the font is not sized from
        // the (much taller) full coverage box. This takes priority over the line-bounds median.
        if (block.SourceGlyphHeight is { } glyphHeight && glyphHeight > 0)
            return glyphHeight / _dpiY;

        if (block.SourceLineBounds is not { Count: > 0 })
            return fallbackHeight;

        var lineHeights = block.SourceLineBounds
            .Select(bounds => bounds.Height / _dpiY)
            .OrderBy(height => height)
            .ToList();
        return lineHeights[lineHeights.Count / 2];
    }

    // No height test of its own — SourceFontScale fades the boost out with height, so gating it
    // here too would put back a step at whatever height the gate used.
    private bool IsLatinSourceToCjkTarget() =>
        _currentSourceLanguage.Equals("EN", StringComparison.OrdinalIgnoreCase) &&
        IsCjkLanguage(_currentTargetLanguage);

    private static bool IsCjkLanguage(string language) =>
        language.Equals("ZH", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ZH-", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("JA", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("KO", StringComparison.OrdinalIgnoreCase);

    private double FindLargestGroupedFontSize(
        string text,
        Typeface typeface,
        double preferredFontSize,
        double minimumFontSize,
        double maxTextWidth,
        int maxLineCount,
        double maxBorderHeight)
    {
        for (double size = preferredFontSize; size >= minimumFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        for (double size = minimumFontSize - 0.5; size >= GroupedEmergencyMinFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        return GroupedEmergencyMinFontSize;
    }

    private int EstimateWrappedLineCount(string text, Typeface typeface, double fontSize, double maxTextWidth)
    {
        var singleLine = MeasureText(text, typeface, fontSize);
        if (singleLine.Width <= maxTextWidth)
            return 1;

        var wrapped = MeasureText(text, typeface, fontSize, maxTextWidth);
        var lineHeight = Math.Max(1, MeasureText("Ag", typeface, fontSize).Height);
        return Math.Max(1, (int)Math.Ceiling((wrapped.Height - 0.1) / lineHeight));
    }

    private double GetBottomAvailableHeight(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfW,
        double selScreenY,
        double selScreenHeight,
        double canvasHeight)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentRight = currentLeft + wpfW + BubbleExpand * 2;
        double selectionTop = (selScreenY - _physBounds.Top) / _dpiY;
        double selectionBottom = selectionTop + selScreenHeight / _dpiY;
        double bottomLimit = Math.Min(canvasHeight - OverlayPadding, selectionBottom);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.Y <= current.Bounds.Y)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / _dpiX - BubbleExpand;
            double otherRight = otherLeft + other.Bounds.Width / _dpiX + BubbleExpand * 2;
            bool overlapsHorizontally = otherLeft < currentRight && otherRight > currentLeft;
            if (!overlapsHorizontally)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / _dpiY - OverlayPadding;
            bottomLimit = Math.Min(bottomLimit, otherTop);
        }

        return Math.Max(0, bottomLimit - (canvasY - BubbleExpand));
    }

    private static bool HasLowerOverlappingBlock(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks)
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

    private double GetRightExpansionWidth(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfH,
        double selScreenX,
        double selScreenWidth,
        double canvasWidth)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentTop = canvasY - BubbleExpand;
        double currentBottom = currentTop + wpfH + BubbleExpand * 2;
        double selectionLeft = (selScreenX - _physBounds.Left) / _dpiX;
        double selectionRight = selectionLeft + selScreenWidth / _dpiX;
        double rightLimit = Math.Min(canvasWidth - OverlayPadding, selectionRight);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.X <= current.Bounds.X)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / _dpiY - BubbleExpand;
            double otherBottom = otherTop + other.Bounds.Height / _dpiY + BubbleExpand * 2;
            bool overlapsVertically = otherTop < currentBottom && otherBottom > currentTop;
            if (!overlapsVertically)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / _dpiX - BubbleExpand;
            rightLimit = Math.Min(rightLimit, otherLeft - OverlayPadding);
        }

        return Math.Max(0, rightLimit - currentLeft);
    }

    private FormattedText MeasureText(string text, Typeface typeface, double fontSize, double? maxTextWidth = null)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.Black,
            _dpiY);

        if (maxTextWidth.HasValue)
            formattedText.MaxTextWidth = maxTextWidth.Value;

        return formattedText;
    }

    // Esc is handled by the session-wide GlobalEscapeHook, not here — the overlay only exists
    // once a selection has been drawn, so hosting the hook would leave Esc dead until then.
    public void CloseOverlay() => Close();
}
