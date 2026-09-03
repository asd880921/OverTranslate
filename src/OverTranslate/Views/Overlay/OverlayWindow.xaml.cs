using System.Buffers;
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

    // Debug geometry, drawn only when the setting asks for it. The two layers nest, so they are
    // given different weights as well as different colours: the recogniser's lines are a thin solid
    // box, and the group that owns them a dashed one just outside. Alpha low enough that the
    // capture underneath still reads, which is the whole point of looking at it.
    private const byte DebugFillAlpha = 0x26;
    private static readonly System.Windows.Media.Color OcrLineBoxColor = System.Windows.Media.Color.FromRgb(0x4D, 0xA3, 0xFF);
    private static readonly System.Windows.Media.Color TextGroupBoxColor = System.Windows.Media.Color.FromRgb(0xFF, 0xB4, 0x54);

    private bool _isLoaded;
    private List<TranslatedBlock> _currentBlocks;
    private IReadOnlyList<OcrTextBlock> _currentOcrBlocks;
    private double _currentSelectionScreenX;
    private double _currentSelectionScreenY;
    private double _currentSelectionScreenWidth;
    private double _currentSelectionScreenHeight;
    private string _currentSourceLanguage;
    private string _currentTargetLanguage;
    private bool _currentVerticalText;
    private bool _currentComicMode;

    public OverlayWindow(
        List<TranslatedBlock> blocks,
        IReadOnlyList<OcrTextBlock> ocrBlocks,
        double selectionScreenX,
        double selectionScreenY,
        double selectionScreenWidth,
        double selectionScreenHeight,
        string sourceLanguage,
        string targetLanguage,
        bool verticalText,
        bool comicMode)
    {
        InitializeComponent();
        _currentBlocks = blocks;
        _currentOcrBlocks = ocrBlocks;
        _currentSelectionScreenX = selectionScreenX;
        _currentSelectionScreenY = selectionScreenY;
        _currentSelectionScreenWidth = selectionScreenWidth;
        _currentSelectionScreenHeight = selectionScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        _currentVerticalText = verticalText;
        _currentComicMode = comicMode;

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
        DebugCanvas.Children.Clear();

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

    /// <summary>
    /// Takes the reading of a capture, before any translation of it exists.
    /// </summary>
    /// <remarks>
    /// The debug boxes describe the source, not the answer, so they are shown from the moment the
    /// recogniser has finished — through the translating indicator, and on a capture whose
    /// translation failed or was never asked for.
    /// </remarks>
    public void ShowOcrDebug(
        IReadOnlyList<OcrTextBlock> ocrBlocks, double selScreenX, double selScreenY)
    {
        _currentOcrBlocks = ocrBlocks;
        if (_isLoaded)
            BuildDebugBoxes(selScreenX, selScreenY);
    }

    public void UpdateBlocks(
        List<TranslatedBlock> blocks,
        IReadOnlyList<OcrTextBlock> ocrBlocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight,
        string sourceLanguage,
        string targetLanguage,
        bool verticalText,
        bool comicMode)
    {
        _currentBlocks = blocks;
        _currentOcrBlocks = ocrBlocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        _currentSelectionScreenWidth = selScreenWidth;
        _currentSelectionScreenHeight = selScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        _currentVerticalText = verticalText;
        _currentComicMode = comicMode;
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

        // Null only when there is nothing over the capture at all, which is not the same as "no
        // bubbles": under 顯示原文 the debug boxes are still up, and that combination — the original
        // words with the boxes drawn round them — is the one worth sending to somebody. Nobody is
        // in this state by accident.
        var hasBubbles = BubbleBackgroundCanvas.Visibility == Visibility.Visible &&
                         (BubbleBackgroundCanvas.Children.Count > 0 || BubbleTextCanvas.Children.Count > 0);
        var hasDebugBoxes = DebugCanvas.Visibility == Visibility.Visible && DebugCanvas.Children.Count > 0;
        if (!hasBubbles && !hasDebugBoxes) return null;

        int fullW = Math.Max(1, _physBounds.Width);
        int fullH = Math.Max(1, _physBounds.Height);

        // Render the whole overlay content (both bubble layers) at physical resolution. The
        // processing indicator is Collapsed whenever bubbles exist, so it does not appear.
        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
            fullW, fullH, 96 * _dpiX, 96 * _dpiY, System.Windows.Media.PixelFormats.Pbgra32);

        // The debug boxes are included, deliberately. Someone with them switched on is looking at
        // how a capture was read, and the copy is how they show that to somebody else — a picture
        // of the problem without the boxes is a picture of nothing in particular.
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
        BuildDebugBoxes(selScreenX, selScreenY);

        if (_currentVerticalText)
        {
            BuildVerticalOverlay(
                blocks,
                selScreenX,
                selScreenY,
                selScreenWidth,
                selScreenHeight);
            return;
        }

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
                // Growing rightwards is room borrowed from whatever the capture has beside this
                // group, and on a comic page that is artwork rather than empty margin: the group's
                // box is the balloon, and the balloon is the only place the text may be. So comic
                // mode keeps the box it was given and pays for the text in font size instead,
                // which is what a letterer does with a balloon that is already drawn.
                var rightAvailableW = _currentComicMode
                    ? borderW
                    : GetRightExpansionWidth(
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

                // Kept because the wrapped fallback below starts its search from here rather than
                // from what the single-line attempt narrowed the font to. Wrapping trades width for
                // height, so the size that was too wide for one line is often perfectly fine on two.
                var sourceMatchedFontSize = fontSize;

                fontSize = layout.FontSize;
                targetBorderW = layout.BorderWidth;
                preferRightExpansion = layout.PreferRightExpansion;

                var finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                var singleLineInnerWidth = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
                var stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                // Scaling the size by the width ratio treats text width as exactly proportional to
                // font size, and it is not — hinting and rounding leave the result a fraction over
                // often enough to matter. One pass therefore came back still overflowing by a pixel
                // or two, which was enough for CharacterEllipsis to take the line's last character
                // off a line that had every appearance of fitting. Converging instead keeps it, and
                // keeps it on one line, which the wrapped fallback below would not.
                var emergencyFloor = Math.Min(fontSize, SingleLineEmergencyMinFontSize);
                for (var attempt = 0; attempt < 3 && stillOverflowsSingleLine; attempt++)
                {
                    var fitted = Math.Max(
                        emergencyFloor, fontSize * singleLineInnerWidth / finalSingleLineMeasure.Width);
                    if (fitted >= fontSize) break;

                    fontSize = fitted;
                    finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                    stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                }

                if (stillOverflowsSingleLine)
                {
                    // Not even the emergency size fits this on one line, so it wraps. That used to
                    // be conditional — nothing overlapping below, at most two lines, and the result
                    // had to fit the gap — and every case that failed a condition fell through to
                    // CharacterEllipsis, which threw the tail of the sentence away. A paragraph read
                    // as separate lines failed the first condition on all but its last line, so the
                    // commonest shape of text on a page was also the one that lost the most.
                    //
                    // Nothing here is worth losing text over. A bubble that grows too far is visible
                    // and the user can re-select; a trimmed one reads as a finished sentence that
                    // happens to say something else, and there is no sign anything went missing.
                    wrap = true;
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

                    // Largest size whose wrapped form still fits the gap. The line count is left
                    // unbounded because the gap is the real constraint: this bubble replaces one
                    // source line, so every extra line is height borrowed from what sits below.
                    fontSize = FindLargestGroupedFontSize(
                        block.TranslatedText,
                        typeface,
                        sourceMatchedFontSize,
                        SingleLineAbsoluteMinFontSize,
                        singleLineInnerWidth,
                        int.MaxValue,
                        maxBorderHeight);
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

                        // The same non-proportionality the single-line path above converges around,
                        // and this branch never re-measured at all: the size it settled on was taken
                        // on trust, and whatever it was over by came off the end of the line. Wrap
                        // only if converging cannot close the gap.
                        var fitted = MeasureText(block.TranslatedText, typeface, fontSize);
                        for (var attempt = 0; attempt < 3 && fitted.Width > innerW; attempt++)
                        {
                            var next = Math.Max(minFontSize, fontSize * innerW / fitted.Width);
                            if (next >= fontSize) break;

                            fontSize = next;
                            fitted = MeasureText(block.TranslatedText, typeface, fontSize);
                        }

                        if (fitted.Width > innerW) wrap = true;
                    }
                    else
                    {
                        fontSize = Math.Max(WrappedAbsoluteMinFontSize, minFontSize);
                        wrap = true;
                    }
                }
            }

            // A translation can arrive with a line break already in it: a local model that answered
            // over two lines, an engine echoing a break out of the source. NoWrap does not ignore
            // one — the TextBlock still breaks there — and the bubble was only ever built one line
            // tall, so everything after the break was outside it. That is what CharacterEllipsis
            // showed as a "…" at the end of the first line, and it is the shape of the report that
            // opened #73: not a word missing, the rest of the sentence missing.
            //
            // Wrapping is what makes the height below count every line the text really has.
            if (!wrap && HasLineBreak(block.TranslatedText)) wrap = true;

            double actualBorderH = Math.Max(borderH, fontSize + BubbleVerticalPadding);
            if (wrap)
            {
                innerW = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
                var wrapMeasured = MeasureText(block.TranslatedText, typeface, fontSize, innerW);
                actualBorderH = OverlayBubbleHeight.ForWrapped(
                    borderH,
                    actualBorderH,
                    wrapMeasured.Height + BubbleVerticalPadding,
                    maxWrapBorderHeight);
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
                    // Never trimmed. Every path that cannot fit the text on one line now wraps, so
                    // reaching here without wrap means it already fits; leaving CharacterEllipsis on
                    // would only mean that a measurement being a pixel out costs the user a word.
                    TextTrimming = TextTrimming.None,

                    // Centred everywhere the bubble is about as tall as its text, which is every
                    // bubble but a comic one: there the box is the union of a balloon's lines and
                    // the paragraph replacing them is shorter than the lines it replaces, so the
                    // text floats in the middle of a band of empty box. Measured on a stat page
                    // laid out as label / body / label / body — 「額外獎勵」over its two indented
                    // centred lines — the body ended up nearer the label below it than the one it
                    // belongs to, and a list of pairs stopped reading as pairs. Top puts the
                    // paragraph where the balloon's first line was, which is where the reader is
                    // already looking.
                    VerticalAlignment = _currentComicMode
                        ? VerticalAlignment.Top
                        : VerticalAlignment.Center,
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

    private void BuildVerticalOverlay(
        IReadOnlyList<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight)
    {
        double winPhysLeft = _physBounds.Left;
        double winPhysTop = _physBounds.Top;
        double canvasWidth = BubbleBackgroundCanvas.ActualWidth > 0
            ? BubbleBackgroundCanvas.ActualWidth
            : Width;
        double canvasHeight = BubbleBackgroundCanvas.ActualHeight > 0
            ? BubbleBackgroundCanvas.ActualHeight
            : Height;
        double selectionLeft = (selScreenX - winPhysLeft) / _dpiX;
        double selectionTop = (selScreenY - winPhysTop) / _dpiY;
        double selectionRight = selectionLeft + selScreenWidth / _dpiX;
        double selectionBottom = selectionTop + selScreenHeight / _dpiY;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText))
                continue;

            double canvasX = (selScreenX + block.Bounds.X - winPhysLeft) / _dpiX;
            double canvasY = (selScreenY + block.Bounds.Y - winPhysTop) / _dpiY;
            double wpfW = block.Bounds.Width / _dpiX;
            double wpfH = block.Bounds.Height / _dpiY;
            double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
            double borderH = wpfH + BubbleExpand * 2;
            double sourceGlyphSize = GetSourceFontReferenceHeight(block, wpfH);
            string text = new(block.TranslatedText.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var grid = FitVerticalGrid(borderW, borderH, sourceGlyphSize, text.Length);
            double cellSize = grid.CellSize;
            borderH = grid.Height;

            double maxLeft = Math.Min(
                canvasWidth - borderW - OverlayPadding,
                selectionRight - borderW);
            double maxTop = Math.Min(
                canvasHeight - borderH - OverlayPadding,
                selectionBottom - borderH);
            double left = Math.Clamp(
                canvasX - BubbleExpand,
                Math.Max(OverlayPadding, selectionLeft),
                Math.Max(Math.Max(OverlayPadding, selectionLeft), maxLeft));
            double top = Math.Clamp(
                canvasY - BubbleExpand,
                Math.Max(OverlayPadding, selectionTop),
                Math.Max(Math.Max(OverlayPadding, selectionTop), maxTop));

            var background = block.BackgroundColor.A == 0 ? Colors.White : block.BackgroundColor;
            System.Windows.Media.Brush foreground;
            if (block.TextColor.A != 0)
            {
                foreground = new SolidColorBrush(block.TextColor);
            }
            else
            {
                double luminance =
                    (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
                foreground = luminance > 0.5
                    ? System.Windows.Media.Brushes.Black
                    : System.Windows.Media.Brushes.White;
            }

            var backgroundBorder = new Border
            {
                Background = new SolidColorBrush(background),
                Width = borderW,
                Height = borderH,
                ClipToBounds = true,
            };
            Canvas.SetLeft(backgroundBorder, left);
            Canvas.SetTop(backgroundBorder, top);
            BubbleBackgroundCanvas.Children.Add(backgroundBorder);

            var bubbleBounds = new Rect(left, top, borderW, borderH);
            double fontSize = cellSize * 0.92;
            foreach (var (glyph, cellBounds) in VerticalCells(text, bubbleBounds, cellSize))
            {
                var cell = new TextBlock
                {
                    Text = glyph.ToString(),
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = foreground,
                    TextAlignment = TextAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily(
                        "Microsoft JhengHei, Segoe UI, Sans-Serif"),
                };
                if (RotatesInVerticalText(glyph))
                {
                    cell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                    cell.RenderTransform = new RotateTransform(90);
                }

                PositionVerticalGlyph(cell, cellBounds);
                BubbleTextCanvas.Children.Add(cell);
            }
        }
    }

    internal static void PositionVerticalGlyph(TextBlock glyph, Rect bounds)
    {
        glyph.Width = bounds.Width;
        glyph.Height = bounds.Height;
        glyph.LineHeight = bounds.Height;
        glyph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Canvas.SetLeft(glyph, bounds.X);
        Canvas.SetTop(glyph, bounds.Y);
    }

    private static int VerticalCapacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));

    internal static (double CellSize, double Height) FitVerticalGrid(
        double width,
        double height,
        double preferredCellSize,
        int characterCount)
    {
        int needed = Math.Max(1, characterCount);
        double cellSize = Math.Max(SingleLineAbsoluteMinFontSize, preferredCellSize);
        while (cellSize > SingleLineEmergencyMinFontSize &&
               VerticalCapacity(width, height, cellSize) < needed)
        {
            cellSize = Math.Max(SingleLineEmergencyMinFontSize, cellSize - 0.5);
        }

        int columns = Math.Max(1, (int)Math.Floor(width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling((double)needed / columns));
        return (cellSize, Math.Max(height, rows * cellSize));
    }

    private static readonly SearchValues<char> RotatedVerticalGlyphs = SearchValues.Create(
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        "…⋯‥");

    internal static bool RotatesInVerticalText(char glyph) =>
        RotatedVerticalGlyphs.Contains(glyph);

    /// <summary>Returns cells in vertical reading order: downwards, then one column left.</summary>
    internal static IEnumerable<(char Glyph, Rect Cell)> VerticalCells(
        string text,
        Rect bounds,
        double cellSize)
    {
        int columns = Math.Max(1, (int)Math.Floor(bounds.Width / cellSize));
        int rows = Math.Max(1, (int)Math.Floor((bounds.Height + 0.01) / cellSize));

        for (int i = 0; i < text.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;
            if (column >= columns)
                yield break;

            yield return (text[i], new Rect(
                bounds.Left + bounds.Width - (column + 1) * cellSize,
                bounds.Top + row * cellSize,
                cellSize,
                cellSize));
        }
    }

    private void SetTranslationLayersVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BubbleBackgroundCanvas.Visibility = visibility;
        BubbleTextCanvas.Visibility = visibility;
        // DebugCanvas is deliberately not switched with them. These boxes are drawn around the
        // source text, so 顯示原文 is the moment they are most worth seeing — the boxes and the words
        // they were measured from, together.
    }

    /// <summary>
    /// Draws the OCR geometry the debug setting asks for, in the selection's own coordinates.
    /// </summary>
    /// <remarks>
    /// Groups first so the lines land on top of them: where the two coincide the finer box is the
    /// one worth reading, and it is the one that says what the recogniser actually returned.
    /// </remarks>
    private void BuildDebugBoxes(double selScreenX, double selScreenY)
    {
        DebugCanvas.Children.Clear();

        var debug = SettingsService.Instance.Current.OcrDebug;
        if (_currentOcrBlocks.Count == 0) return;

        if (debug.ShowGroupBoxes)
            foreach (var box in OcrDebugBoxes.GroupBoxes(_currentOcrBlocks))
                DebugCanvas.Children.Add(
                    CreateDebugBox(box, selScreenX, selScreenY, TextGroupBoxColor, dashed: true));

        if (debug.ShowLineBoxes)
            foreach (var box in OcrDebugBoxes.LineBoxes(_currentOcrBlocks))
                DebugCanvas.Children.Add(
                    CreateDebugBox(box, selScreenX, selScreenY, OcrLineBoxColor, dashed: false));
    }

    private System.Windows.Shapes.Rectangle CreateDebugBox(
        Rect box, double selScreenX, double selScreenY, System.Windows.Media.Color color, bool dashed)
    {
        var fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(DebugFillAlpha, color.R, color.G, color.B));
        var stroke = new SolidColorBrush(color);
        fill.Freeze();
        stroke.Freeze();

        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, box.Width / _dpiX),
            Height = Math.Max(1, box.Height / _dpiY),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = dashed ? 1.5 : 1,
            StrokeDashArray = dashed ? new DoubleCollection([4, 3]) : null,
            RadiusX = 3,
            RadiusY = 3,
        };

        Canvas.SetLeft(shape, (selScreenX + box.X - _physBounds.Left) / _dpiX);
        Canvas.SetTop(shape, (selScreenY + box.Y - _physBounds.Top) / _dpiY);
        return shape;
    }

    private static bool HasLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

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
