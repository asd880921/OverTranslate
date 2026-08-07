using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Layout;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, and System.Drawing arrives with
// it — both carry a type of these names.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The live subtitle layer for one watched block: a click-through window pinned over the region,
/// drawing each translated line as plain text on a black scrim exactly where its source sits.
/// </summary>
/// <remarks>
/// No bubbles and no sampled colours here, unlike the screenshot overlay. That overlay produces a
/// still the user studies, so it is worth matching the page's own background; this one sits over
/// content that is still moving, where a sampled colour would be wrong a frame later. A fixed black
/// scrim is only as wide as the text it has to hide, which is what lets the user draw a block
/// deliberately larger than the text without the surrounding picture being covered up.
/// </remarks>
public partial class RealtimeBlockWindow : Window
{
    // The scrim exists to hide the source line underneath, so it is sized from the text and not the
    // block: everything outside these paddings stays untouched picture.
    private const double ScrimPaddingX = 5;
    private const double ScrimPaddingY = 2;
    private const double MinFontSize = 8.5;
    private const double LineHeightRatio = 1.32;

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(110));

    private static readonly SolidColorBrush ScrimBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0)));
    private static readonly SolidColorBrush TextBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)));
    private static readonly FontFamily TextFont =
        new("Microsoft JhengHei, Segoe UI Variable Text, Segoe UI, Sans-Serif");

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x8000000;

    private readonly System.Drawing.Rectangle _physBounds;
    private readonly bool _latinSourceToCjkTarget;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private bool _isLoaded;

    private IReadOnlyList<TranslatedBlock> _lines = [];

    public RealtimeBlockWindow(
        int regionId,
        System.Drawing.Rectangle physBounds,
        string sourceLanguage,
        string targetLanguage)
    {
        InitializeComponent();

        RegionId = regionId;
        _physBounds = physBounds;
        _latinSourceToCjkTarget = IsLatinToCjk(sourceLanguage, targetLanguage);

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }
            _isLoaded = true;
            Rebuild();
        };
    }

    public int RegionId { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        // Transparent so clicks reach the application being watched, NoActivate so appearing over a
        // game never takes its focus — the block has to be furniture, not a window.
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);

        // Before the DPI is read in Loaded: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);

        // Without this the next poll would read this window's own translation back off the screen.
        WindowCaptureShield.Exclude(this);
    }

    public void SetLines(IReadOnlyList<TranslatedBlock> lines)
    {
        _lines = lines;
        if (_isLoaded) Rebuild();
    }

    private void Rebuild()
    {
        LineCanvas.Children.Clear();

        double canvasWidth = _physBounds.Width / _dpiX;
        double canvasHeight = _physBounds.Height / _dpiY;

        foreach (var line in _lines)
        {
            if (string.IsNullOrWhiteSpace(line.TranslatedText)) continue;
            var visual = BuildLine(line, canvasWidth, canvasHeight);
            if (visual is not null) LineCanvas.Children.Add(visual);
        }

        // A short cross-fade rather than an instant swap: at this size a hard cut reads as a flicker
        // in the corner of the eye, which is exactly where this content lives.
        LineCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = FadeDuration
        });
    }

    private UIElement? BuildLine(TranslatedBlock line, double canvasWidth, double canvasHeight)
    {
        double left = line.Bounds.X / _dpiX;
        double top = line.Bounds.Y / _dpiY;
        double sourceWidth = line.Bounds.Width / _dpiX;
        double sourceHeight = line.Bounds.Height / _dpiY;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        // Grouped sources carry several source lines under one translation, so their text wraps and
        // the scrim has to cover the whole group. A single source line never wraps: it is one line
        // on screen and stays one line.
        bool isGrouped = line.SourceLineBounds is { Count: > 1 };

        double glyphHeight = GetGlyphHeight(line, sourceHeight);
        double fontSize = SourceFontScale.Calculate(glyphHeight, _latinSourceToCjkTarget);

        double maxWidth = Math.Max(20, canvasWidth - left - 2);
        var typeface = new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        double textWidth;
        double textHeight;
        if (isGrouped)
        {
            // Keep the group's own width and let the translation wrap inside it, shrinking only if
            // wrapping alone cannot make it fit the source's height.
            textWidth = Math.Min(maxWidth, Math.Max(sourceWidth, 20));
            fontSize = FitWrapped(line.TranslatedText, typeface, fontSize, textWidth, sourceHeight);
            textHeight = Measure(line.TranslatedText, typeface, fontSize, textWidth).Height;
        }
        else
        {
            var measured = Measure(line.TranslatedText, typeface, fontSize, null);
            if (measured.Width > maxWidth)
            {
                // Shrink to fit the room to the right of the source, down to the readability floor.
                // Past that the line is trimmed instead: an unreadable full line helps nobody.
                fontSize = Math.Max(MinFontSize, fontSize * maxWidth / measured.Width);
                measured = Measure(line.TranslatedText, typeface, fontSize, null);
            }
            textWidth = Math.Min(maxWidth, measured.Width);
            textHeight = measured.Height;
        }

        double scrimWidth = textWidth + ScrimPaddingX * 2;
        double scrimHeight = Math.Max(sourceHeight, textHeight) + ScrimPaddingY * 2;

        // The scrim covers the source, so it grows around the source's own centre line rather than
        // hanging off its top edge.
        double scrimLeft = Math.Clamp(left - ScrimPaddingX, 0, Math.Max(0, canvasWidth - scrimWidth));
        double scrimTop = Math.Clamp(
            top + sourceHeight / 2 - scrimHeight / 2, 0, Math.Max(0, canvasHeight - scrimHeight));

        var scrim = new Border
        {
            Width = scrimWidth,
            Height = scrimHeight,
            Background = ScrimBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(ScrimPaddingX, ScrimPaddingY, ScrimPaddingX, ScrimPaddingY),
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = line.TranslatedText,
                FontFamily = TextFont,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextWrapping = isGrouped ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = isGrouped ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        Canvas.SetLeft(scrim, scrimLeft);
        Canvas.SetTop(scrim, scrimTop);
        return scrim;
    }

    // Largest size at which the wrapped translation still fits the height its source occupied, so a
    // grouped block does not push its scrim over whatever sits below it.
    private double FitWrapped(
        string text, Typeface typeface, double preferredFontSize, double width, double maxHeight)
    {
        for (double size = preferredFontSize; size >= MinFontSize; size -= 0.5)
            if (Measure(text, typeface, size, width).Height <= maxHeight)
                return size;

        return MinFontSize;
    }

    private double GetGlyphHeight(TranslatedBlock line, double fallbackHeight)
    {
        // Latin sources carry the real glyph height separately: their detection box is much taller
        // than the text in it, and sizing the font from the box would render the translation far
        // larger than what it replaces.
        if (line.SourceGlyphHeight is { } glyphHeight && glyphHeight > 0)
            return glyphHeight / _dpiY;

        if (line.SourceLineBounds is not { Count: > 0 } lineBounds)
            return fallbackHeight;

        var heights = lineBounds.Select(bounds => bounds.Height / _dpiY).OrderBy(height => height).ToList();
        return heights[heights.Count / 2];
    }

    private Size Measure(string text, Typeface typeface, double fontSize, double? maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            _dpiY);

        if (maxWidth.HasValue) formatted.MaxTextWidth = maxWidth.Value;

        // FormattedText reports the ink height, which for a single line of CJK sits noticeably
        // below the line box the TextBlock will actually lay out. Sizing the scrim from the ink
        // would clip descenders on the very first line.
        return new Size(formatted.Width, Math.Max(formatted.Height, fontSize * LineHeightRatio));
    }

    private static bool IsLatinToCjk(string sourceLanguage, string targetLanguage) =>
        sourceLanguage.Equals("EN", StringComparison.OrdinalIgnoreCase) &&
        (targetLanguage.StartsWith("ZH", StringComparison.OrdinalIgnoreCase) ||
         targetLanguage.Equals("JA", StringComparison.OrdinalIgnoreCase) ||
         targetLanguage.Equals("KO", StringComparison.OrdinalIgnoreCase));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
