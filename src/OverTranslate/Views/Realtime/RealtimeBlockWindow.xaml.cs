using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    // block: everything outside these paddings stays untouched picture. The vertical padding is the
    // tighter of the two on purpose — a band reaching above and below a subtitle sits in the middle
    // of what the user is watching, while the same slack to its left and right lands on picture they
    // are not reading anyway.
    private const double ScrimPaddingX = 5;
    private const double ScrimPaddingY = 1;

    private const double MinFontSize = 8.5;
    private const double LineHeightRatio = 1.22;

    /// <summary>
    /// How much taller than the line it replaces a single-line translation may be drawn. The scrim
    /// is sized to whichever is larger, so this is really a cap on the band's height.
    /// </summary>
    private const double MaxHeightOverSource = 1.15;

    // No fade on rebuild. A cross-fade was there to soften the swap, but every repaint took the
    // whole layer to transparent and back, so a line being re-read — which happens several times
    // while one subtitle is on screen — pulsed the text the reader was in the middle of. Swapping
    // outright is the quieter of the two, and the repaints it makes visible are better dealt with
    // by not making them: see RealtimeBlockWindow.SetLines and TextSimilarity.
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
        // A rebuild cross-fades, so repainting an unchanged overlay is a visible flicker for no
        // gain. The session already suppresses re-translation of text that has not really changed;
        // this catches what is left — the same translation arriving with its boxes a pixel or two
        // off, which happens whenever recognition redraws its idea of where the line sits.
        if (_isLoaded && LooksIdentical(_lines, lines)) return;

        _lines = lines;
        if (_isLoaded) Rebuild();
    }

    // A pixel of movement is below what anyone can see and well inside recognition's own precision.
    private const double SamePositionTolerance = 2.0;

    private static bool LooksIdentical(IReadOnlyList<TranslatedBlock> current, IReadOnlyList<TranslatedBlock> next)
    {
        if (current.Count != next.Count) return false;

        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].TranslatedText, next[i].TranslatedText, StringComparison.Ordinal))
                return false;

            var a = current[i].Bounds;
            var b = next[i].Bounds;
            if (Math.Abs(a.X - b.X) > SamePositionTolerance ||
                Math.Abs(a.Y - b.Y) > SamePositionTolerance ||
                Math.Abs(a.Width - b.Width) > SamePositionTolerance ||
                Math.Abs(a.Height - b.Height) > SamePositionTolerance)
                return false;
        }

        return true;
    }

    private void Rebuild()
    {
        ScrimCanvas.Children.Clear();
        TextCanvas.Children.Clear();

        double canvasWidth = _physBounds.Width / _dpiX;
        double canvasHeight = _physBounds.Height / _dpiY;

        foreach (var line in _lines)
        {
            if (string.IsNullOrWhiteSpace(line.TranslatedText)) continue;
            if (BuildLine(line, canvasWidth, canvasHeight) is not { } visual) continue;

            ScrimCanvas.Children.Add(visual.Scrim);
            TextCanvas.Children.Add(visual.Text);
        }
    }

    /// <summary>One line's two halves, drawn into separate layers so text always wins over scrims.</summary>
    private readonly record struct LineVisual(UIElement Scrim, UIElement Text);

    private LineVisual? BuildLine(TranslatedBlock line, double canvasWidth, double canvasHeight)
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

        // How tall the line being replaced actually is, which is not the same as how tall its
        // detection box is. A Latin source arrives with the full box — deliberately, because the
        // screenshot overlay wants it as coverage area — and that box runs about half again as tall
        // as the glyphs in it. A CJK source arrives with its box already shrunk onto the glyphs and
        // recentred, which is why picking Korean by mistake over English subtitles drew a visibly
        // tighter band than picking English did: 56px against 88px over the same 46px of text.
        //
        // The band exists to hide one line of text, and a band twice the height of that line is
        // exactly what this overlay's whole approach is meant to avoid — everything outside it is
        // supposed to stay picture the user is watching.
        double lineHeight = Math.Min(sourceHeight, glyphHeight * LineHeightRatio);

        // The whole block, not just the room to the right of the source's left edge: a band centred
        // on its source grows in both directions, so what bounds it is the block, and RealtimeBandPlacement
        // is what keeps it inside. Leaving the scrim's own padding out means the band fits exactly.
        double maxWidth = Math.Max(20, canvasWidth - ScrimPaddingX * 2);
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
            // Sized to the line it replaces rather than to whatever the font scale would prefer.
            // The scale exists for the screenshot overlay, where a still is studied and a larger
            // translation is welcome; here the scrim it forces is a band across live content the
            // user is trying to watch, so a translation half again as tall as the line underneath
            // buys legibility with the picture.
            fontSize = Math.Max(MinFontSize, Math.Min(fontSize, lineHeight * MaxHeightOverSource / LineHeightRatio));

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

        // A grouped block's bounds are the union of its lines, which is the area that has to be
        // covered and is not an inflated single box, so it keeps using them.
        double coverHeight = isGrouped ? sourceHeight : lineHeight;
        double scrimHeight = Math.Max(coverHeight, textHeight) + ScrimPaddingY * 2;

        // The scrim covers the source, so it grows around the source's own centre — horizontally as
        // well as vertically — rather than hanging off its top-left corner.
        double scrimLeft = RealtimeBandPlacement.Left(left, sourceWidth, scrimWidth, canvasWidth);
        double scrimTop = Math.Clamp(
            top + sourceHeight / 2 - scrimHeight / 2, 0, Math.Max(0, canvasHeight - scrimHeight));

        var scrim = new Border
        {
            Width = scrimWidth,
            Height = scrimHeight,
            Background = ScrimBrush,
            CornerRadius = new CornerRadius(3),
        };

        // Same geometry, no background: the two are stacked in separate layers, so the text has to
        // carry its own box to land in exactly the place the scrim covers.
        var text = new Border
        {
            Width = scrimWidth,
            Height = scrimHeight,
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

        foreach (var element in (UIElement[])[scrim, text])
        {
            Canvas.SetLeft(element, scrimLeft);
            Canvas.SetTop(element, scrimTop);
        }

        return new LineVisual(scrim, text);
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
