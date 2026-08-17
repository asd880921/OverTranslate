using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, and System.Drawing arrives with
// it — both carry a type of these names.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The live subtitle layer for one watched block: a click-through window pinned over the region.
/// In natural mode it captures the application underneath, removes the source glyph rectangle with
/// a lightweight local background repair, then draws the translation back into the same place.
/// </summary>
/// <remarks>
/// The background patch is refreshed while a line is visible. That matters over video/game content:
/// a one-time screenshot would turn the translated line into a frozen rectangular tile while the
/// picture behind it kept moving. The overlay itself is excluded from capture, so every refresh sees
/// the original application rather than recursively photographing its own translation.
///
/// That refresh runs off the dispatcher, and only the assignment of the finished brushes comes back
/// to it. Everything before that — the grab, the repair, the bitmap — is pixel work that does not
/// need the interface thread, and leaving it there coupled how smooth the shell felt to how much this
/// layer happened to be doing. It also declines to do the work at all while the picture underneath is
/// holding still, which over a dialogue box or a menu is nearly all of the time.
/// </remarks>
public partial class RealtimeBlockWindow : Window
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    // The scrim exists to hide the source line underneath, so it is sized from the text and not the
    // block: everything outside these paddings stays untouched picture. The vertical padding is the
    // tighter of the two on purpose — a band reaching above and below a subtitle sits in the middle
    // of what the user is watching, while the same slack to its left and right lands on picture they
    // are not reading anyway.
    private const double ScrimPaddingX = 5;
    private const double ScrimPaddingY = 3;

    // The OCR box can omit detached punctuation/diacritics (especially Japanese dakuten). The
    // repaired background therefore extends beyond the visible translation band. Pixels in this
    // guard are copied from the original frame unchanged unless the adaptive eraser identifies them
    // as part of the source line, so the larger patch does not look like a larger subtitle panel.
    private const double MinNaturalGuardX = 10;
    private const double MinNaturalGuardY = 12;

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
    private static readonly FontFamily TextFont =
        new("Microsoft JhengHei, Segoe UI Variable Text, Segoe UI, Sans-Serif");

    // Natural mode uses a real patch of the application under the source text. Refreshing at the
    // same cadence as the screen watcher keeps that patch moving with video without adding another
    // high-frequency rendering loop. No OCR or translation happens here.
    private static readonly TimeSpan NaturalRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly System.Drawing.Rectangle _physBounds;
    private readonly bool _latinSourceToCjkTarget;

    // Per session rather than per application: the colours are read once when the session starts and
    // cannot change while it runs, because reaching the page that sets them means the shell window,
    // which a running session has hidden. Frozen for the same reason the fixed brushes were — they
    // are handed to every line of every rebuild and never mutated.
    private readonly SolidColorBrush _scrimBrush;
    private readonly SolidColorBrush _textBrush;

    // 顯示外觀 → 進階選項, both off unless the user asked for them. Held per session for the same
    // reason the colours are: the page that sets them is behind a shell window this session hid.
    //
    // With both off nothing here grabs the screen at all — see Rebuild. That is not merely a saving:
    // the band is the predictable one of the two looks, and a window that does not photograph itself
    // cannot bake its own translation into what it draws either.
    private readonly bool _naturalBackground;
    private readonly bool _sampleTextColor;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private bool _isLoaded;

    // Published by Rebuild on the dispatcher and read by the refresh loop on its own thread, so it is
    // replaced wholesale rather than mutated. The generation beside it is what a finished batch of
    // brushes is checked against: a rebuild between the grab and the assignment means those brushes
    // belong to visuals that are no longer on the canvas.
    private NaturalPatchVisual[] _naturalPatches = [];
    private int _patchGeneration;

    private CancellationTokenSource? _naturalRefreshCts;

    // What the patches on screen were painted from. Only the refresh loop reads or writes it, except
    // for Rebuild clearing it to say the comparison has to start again.
    private FrameFingerprint? _lastRefreshPrint;

    private IReadOnlyList<TranslatedBlock> _lines = [];

    public RealtimeBlockWindow(
        int regionId,
        System.Drawing.Rectangle physBounds,
        string sourceLanguage,
        string targetLanguage,
        string textColor,
        string scrimColor,
        int scrimOpacity,
        bool naturalBackground = false,
        bool sampleTextColor = false)
    {
        InitializeComponent();

        RegionId = regionId;
        _physBounds = physBounds;
        _naturalBackground = naturalBackground;
        _sampleTextColor = sampleTextColor;
        _latinSourceToCjkTarget = IsLatinToCjk(sourceLanguage, targetLanguage);
        _textBrush = Freeze(new SolidColorBrush(RealtimeSubtitleColors.Text(textColor)));
        _scrimBrush = Freeze(new SolidColorBrush(
            RealtimeSubtitleColors.Scrim(scrimColor, scrimOpacity)));

        Closed += (_, _) => StopNaturalRefresh();

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }
            _isLoaded = true;
            Rebuild();
            UpdateNaturalRefreshTimer();
        };
    }

    public int RegionId { get; }

    /// <summary>Where this block sits on the screen, in physical pixels — what it was pinned to.</summary>
    public System.Drawing.Rectangle PhysicalBounds => _physBounds;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through so clicks reach the application being watched, NoActivate so appearing over
        // a game never takes its focus — the block has to be furniture, not a window.
        WindowStyles.ApplyClickThrough(this, noActivate: true);

        // Before the DPI is read in Loaded: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);

        // Without this the next poll would read this window's own translation back off the screen.
        WindowCaptureShield.Exclude(this);
    }

    /// <summary>
    /// This block's scrims and text as an image at physical resolution, for compositing onto a
    /// screen grab. Null when there is nothing drawn — a block that has not been translated yet has
    /// nothing to contribute and should leave the grabbed pixels alone.
    /// </summary>
    /// <remarks>
    /// Rendering the visual tree rather than reading it back off the screen is not a workaround for
    /// <see cref="WindowCaptureShield"/> — it is the better source. The window is composed with
    /// per-pixel alpha, so what the compositor put on screen is this layer already blended into
    /// whatever was behind it, while this is the layer itself, with its translucency intact for the
    /// caller to blend deliberately.
    /// </remarks>
    public System.Windows.Media.Imaging.BitmapSource? RenderForCapture()
    {
        if (!_isLoaded) return null;
        if (ScrimCanvas.Children.Count == 0 && TextCanvas.Children.Count == 0) return null;

        var width = Math.Max(1, _physBounds.Width);
        var height = Math.Max(1, _physBounds.Height);

        // 96 * dpi so one bitmap pixel is one screen pixel: the canvases are laid out in DIP, and
        // the block's own bounds are in physical pixels.
        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96 * _dpiX, 96 * _dpiY, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(LayerHost);
        rendered.Freeze();
        return rendered;
    }

    public void SetLines(IReadOnlyList<TranslatedBlock> lines)
    {
        // A rebuild cross-fades, so repainting an unchanged overlay is a visible flicker for no
        // gain. The session already suppresses re-translation of text that has not really changed;
        // this catches what is left — the same translation arriving with its boxes a pixel or two
        // off, which happens whenever recognition redraws its idea of where the line sits.
        if (_isLoaded && LooksIdentical(_lines, lines)) return;

        _lines = lines;
        if (_isLoaded)
        {
            Rebuild();
            UpdateNaturalRefreshTimer();
        }
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

        // Moved on before anything is drawn, so a batch of brushes already in flight for the previous
        // arrangement can be told it has been overtaken.
        Interlocked.Increment(ref _patchGeneration);
        var patches = new List<NaturalPatchVisual>();

        double canvasWidth = _physBounds.Width / _dpiX;
        double canvasHeight = _physBounds.Height / _dpiY;

        // Grabbed only if something is going to read it. With 進階選項 off this window draws from the
        // two colours alone, exactly as it did before those switches existed.
        using var frame = _naturalBackground || _sampleTextColor
            ? CaptureUnderlyingRegion()
            : null;

        foreach (var line in _lines)
        {
            if (string.IsNullOrWhiteSpace(line.TranslatedText)) continue;
            if (BuildLine(line, canvasWidth, canvasHeight, frame) is not { } visual) continue;

            ScrimCanvas.Children.Add(visual.Background);
            TextCanvas.Children.Add(visual.Text);

            // Only the repaired backgrounds are worth revisiting: a band drawn in a fixed colour has
            // nothing to follow, and recording one here would have the refresh below replace it with
            // a patch the user never asked for.
            if (_naturalBackground)
                patches.Add(new NaturalPatchVisual(visual.Background, line, visual.PatchBounds));
        }

        Volatile.Write(ref _naturalPatches, [.. patches]);

        // A new set of patches has to be painted once before there is anything to compare against.
        Volatile.Write(ref _lastRefreshPrint, null);
    }

    /// <summary>One line's background patch and translated text, kept in separate layers.</summary>
    private readonly record struct LineVisual(
        Border Background, Border Text, System.Drawing.Rectangle PatchBounds);

    private readonly record struct NaturalPatchVisual(
        Border Surface, TranslatedBlock Line, System.Drawing.Rectangle PatchBounds);

    private LineVisual? BuildLine(
        TranslatedBlock line, double canvasWidth, double canvasHeight, System.Drawing.Bitmap? frame)
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

        // This window is exactly the block the user drew, so a band taller than this is not merely
        // untidy — the part past the edge is not rendered at all. Only the wrapped fallback below
        // needs it; a single line is bounded by its source's own height long before it gets close.
        double maxTextHeight = Math.Max(lineHeight, canvasHeight - ScrimPaddingY * 2);
        var typeface = new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        double textWidth;
        double textHeight;
        // A grouped block wraps by definition; a single source line only wraps when it has run out
        // of every other way to stay whole, which the branch below decides.
        bool wrapped = isGrouped;
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

            // Kept because the wrapped fallback searches from here, not from whatever the width
            // shrink below left behind: wrapping buys width back, so a size that was too wide for
            // one line is often comfortable across two.
            double sourceMatchedFontSize = fontSize;

            var measured = Measure(line.TranslatedText, typeface, fontSize, null);
            if (measured.Width > maxWidth)
            {
                // Shrink to fit the room to the right of the source, down to the readability floor.
                fontSize = Math.Max(MinFontSize, fontSize * maxWidth / measured.Width);
                measured = Measure(line.TranslatedText, typeface, fontSize, null);
            }

            if (measured.Width > maxWidth)
            {
                // At the readability floor and still wider than the block. This used to be where the
                // line was trimmed, on the reasoning that an unreadable full line helps nobody — but
                // the line that got trimmed was readable, just too long, and what it turned into was
                // a sentence that ends early with nothing to say so. Over live video there is no
                // still to go back to and no way to notice, which makes it the worse of the two.
                //
                // So it wraps instead. The band grows, bounded by the block the user drew.
                wrapped = true;
                fontSize = FitWrapped(
                    line.TranslatedText, typeface, sourceMatchedFontSize, maxWidth, maxTextHeight);

                // The band is only as wide as the widest line the wrap actually produced, not the
                // whole block — the same reason a single line is not stretched to fill it. One pixel
                // of slack so rounding cannot leave the TextBlock a hair narrower than the measure.
                var acrossTheBlock = Measure(line.TranslatedText, typeface, fontSize, maxWidth);
                textWidth = Math.Min(maxWidth, acrossTheBlock.Width + 1);

                // Measured again at the width the TextBlock is actually given. Narrowing the band to
                // the widest line can change where a line breaks, and a height taken from the wider
                // measurement would then be one line short — with the box clipping, that is the
                // trimmed tail back again by another route.
                textHeight = Measure(line.TranslatedText, typeface, fontSize, textWidth).Height;
            }
            else
            {
                textWidth = Math.Min(maxWidth, measured.Width);
                textHeight = measured.Height;
            }
        }

        double scrimWidth = Math.Min(canvasWidth, Math.Max(textWidth, sourceWidth) + ScrimPaddingX * 2);

        // A grouped block's bounds are the union of its lines, which is the area that has to be
        // covered and is not an inflated single box, so it keeps using them.
        double coverHeight = isGrouped ? sourceHeight : lineHeight;
        double scrimHeight = Math.Max(coverHeight, textHeight) + ScrimPaddingY * 2;

        // The scrim covers the source, so it grows around the source's own centre — horizontally as
        // well as vertically — rather than hanging off its top-left corner.
        double scrimLeft = RealtimeBandPlacement.Left(left, sourceWidth, scrimWidth, canvasWidth);
        double scrimTop = Math.Clamp(
            top + sourceHeight / 2 - scrimHeight / 2, 0, Math.Max(0, canvasHeight - scrimHeight));

        // The band's own geometry, which is also what the repaired patch falls back to. The guard
        // below only exists to hold the repair, so with 進階選項 off these stay as they are and the
        // background is the band this window has always drawn.
        double patchLeft = scrimLeft;
        double patchTop = scrimTop;
        double patchWidth = scrimWidth;
        double patchHeight = scrimHeight;
        ImageBrush? naturalBrush = null;

        if (_naturalBackground)
        {
            double naturalGuardX = Math.Clamp(sourceHeight * 0.20, MinNaturalGuardX, 20);
            double naturalGuardY = Math.Clamp(sourceHeight * 0.40, MinNaturalGuardY, 26);
            double guardLeft = Math.Max(0, scrimLeft - naturalGuardX);
            double guardTop = Math.Max(0, scrimTop - naturalGuardY);
            double guardRight = Math.Min(canvasWidth, scrimLeft + scrimWidth + naturalGuardX);
            double guardBottom = Math.Min(canvasHeight, scrimTop + scrimHeight + naturalGuardY);
            double guardWidth = Math.Max(0, guardRight - guardLeft);
            double guardHeight = Math.Max(0, guardBottom - guardTop);

            naturalBrush = BuildNaturalBrush(
                frame, line, ToPhysicalPatchBounds(guardLeft, guardTop, guardWidth, guardHeight));

            // Capture can fail on protected/UAC surfaces. Keep the compact band in that case rather
            // than painting the much larger guard with a flat colour.
            if (naturalBrush is not null)
            {
                patchLeft = guardLeft;
                patchTop = guardTop;
                patchWidth = guardWidth;
                patchHeight = guardHeight;
            }
        }

        var patchBounds = ToPhysicalPatchBounds(patchLeft, patchTop, patchWidth, patchHeight);

        var background = new Border
        {
            Width = patchWidth,
            Height = patchHeight,
            Background = (System.Windows.Media.Brush?)naturalBrush ?? _scrimBrush,
            // A natural patch should meet the surrounding picture edge-for-edge. Rounded corners
            // would reveal four pieces of the source text underneath.
            CornerRadius = new CornerRadius(0),
        };

        // Sampling is its own switch: with it off the reader's chosen colour is what gets drawn, and
        // with it on that colour is still what an unconvincing sample falls back to.
        var foreground = _textBrush;
        if (_sampleTextColor && frame is not null)
        {
            var sampled = RealtimeNaturalBackground.SampleTextColor(frame, line.Bounds, _textBrush.Color);
            foreground = Freeze(new SolidColorBrush(sampled));
        }

        // Same geometry, no background: the two are stacked in separate layers, so the text has to
        // carry its own box to land in exactly the place the repaired background covers.
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
                Foreground = foreground,
                TextWrapping = wrapped ? TextWrapping.Wrap : TextWrapping.NoWrap,
                // Never trimmed. A line that cannot fit on one line has already been switched to
                // wrapping above, so getting here without it means the text fits; leaving
                // CharacterEllipsis on would only mean a measurement a pixel out costs a word.
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        Canvas.SetLeft(background, patchLeft);
        Canvas.SetTop(background, patchTop);
        Canvas.SetLeft(text, scrimLeft);
        Canvas.SetTop(text, scrimTop);

        return new LineVisual(background, text, patchBounds);
    }

    private void UpdateNaturalRefreshTimer()
    {
        // Never runs with the switch off: the patches it would refresh are only recorded when the
        // repair is what drew them.
        if (!_naturalBackground || !_isLoaded || Volatile.Read(ref _naturalPatches).Length == 0)
            StopNaturalRefresh();
        else
            StartNaturalRefresh();
    }

    private void StartNaturalRefresh()
    {
        if (_naturalRefreshCts is not null) return;

        var cts = new CancellationTokenSource();
        _naturalRefreshCts = cts;
        _ = Task.Run(() => RefreshNaturalBackgroundsAsync(cts.Token), cts.Token);
    }

    private void StopNaturalRefresh()
    {
        _naturalRefreshCts?.Cancel();
        _naturalRefreshCts?.Dispose();
        _naturalRefreshCts = null;
    }

    /// <summary>
    /// Keeps the repaired backgrounds following the picture underneath, off the dispatcher.
    /// </summary>
    /// <remarks>
    /// The only step here that needs the interface thread is the last one, so it is the only step that
    /// goes back to it: the grab is a BitBlt, the repair is pixel arithmetic, and the brush is frozen
    /// before it leaves this thread. What used to be a DispatcherTimer doing all of it is why a
    /// session with three blocks made the shell feel heavy.
    ///
    /// A tick that finds the picture holding still stops after the fingerprint. That is the ordinary
    /// case for the content this mode suits best — a dialogue panel, a menu, a paused scene — and the
    /// patch on screen is still correct for it, so there is nothing to pay for.
    /// </remarks>
    private async Task RefreshNaturalBackgroundsAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(NaturalRefreshInterval);

            while (await timer.WaitForNextTickAsync(token))
            {
                var generation = Volatile.Read(ref _patchGeneration);
                var patches = Volatile.Read(ref _naturalPatches);
                if (patches.Length == 0) continue;

                using var frame = CaptureUnderlyingRegion();
                if (frame is null) continue;

                // Summarised over the patch rectangles rather than the whole block: a change in a
                // corner of the region the band does not cover is not a reason to repaint it.
                var print = FrameFingerprint.Capture(
                    frame, [.. patches.Select(patch => patch.PatchBounds)]);
                if (print.StillLooksLike(Volatile.Read(ref _lastRefreshPrint))) continue;

                var repainted = new List<(Border Surface, ImageBrush Brush)>(patches.Length);
                foreach (var patch in patches)
                {
                    token.ThrowIfCancellationRequested();
                    if (BuildNaturalBrush(frame, patch.Line, patch.PatchBounds) is { } brush)
                        repainted.Add((patch.Surface, brush));
                }

                Volatile.Write(ref _lastRefreshPrint, print);
                if (repainted.Count == 0) continue;

                await Dispatcher.InvokeAsync(() =>
                {
                    // A rebuild while this batch was being painted means these surfaces are no longer
                    // the ones on the canvas.
                    if (Volatile.Read(ref _patchGeneration) != generation) return;

                    foreach (var (surface, brush) in repainted)
                        surface.Background = brush;
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing or the arrangement changed; nothing to report.
        }
        catch (Exception ex)
        {
            // Nothing above is expected to throw, and this loop is nobody's awaited task — so an
            // exception here would end the refresh silently and leave the patches frozen on the last
            // picture they saw, which looks like the repair itself having failed.
            Log.Warn(ex, "Realtime block {Region} stopped refreshing its repaired background", RegionId);
        }
    }

    private System.Drawing.Bitmap? CaptureUnderlyingRegion()
    {
        if (_physBounds.Width <= 0 || _physBounds.Height <= 0) return null;

        System.Drawing.Bitmap? bitmap = null;
        try
        {
            bitmap = new System.Drawing.Bitmap(
                _physBounds.Width, _physBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                _physBounds.Left, _physBounds.Top, 0, 0, _physBounds.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    private System.Drawing.Rectangle ToPhysicalPatchBounds(
        double left, double top, double width, double height)
    {
        int x1 = Math.Clamp((int)Math.Floor(left * _dpiX), 0, _physBounds.Width);
        int y1 = Math.Clamp((int)Math.Floor(top * _dpiY), 0, _physBounds.Height);
        int x2 = Math.Clamp((int)Math.Ceiling((left + width) * _dpiX), 0, _physBounds.Width);
        int y2 = Math.Clamp((int)Math.Ceiling((top + height) * _dpiY), 0, _physBounds.Height);
        return System.Drawing.Rectangle.FromLTRB(x1, y1, x2, y2);
    }

    private static ImageBrush? BuildNaturalBrush(
        System.Drawing.Bitmap? frame,
        TranslatedBlock line,
        System.Drawing.Rectangle patchBounds)
    {
        if (frame is null || patchBounds.Width <= 0 || patchBounds.Height <= 0) return null;

        var sourceLines = line.SourceLineBounds is { Count: > 0 } lines
            ? lines
            : (IReadOnlyList<System.Windows.Rect>)[line.Bounds];

        using var patch = RealtimeNaturalBackground.CreatePatch(frame, patchBounds, sourceLines);
        if (patch is null) return null;

        var image = BitmapInterop.ToBitmapSource(patch);
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    // Largest size at which the wrapped translation still fits the height it is allowed. For a
    // grouped block that is the height its source occupied, so its scrim does not push over
    // whatever sits below; for a single line forced to wrap it is the whole block, which is this
    // window, so anything taller is cut off by the window edge.
    //
    // Returning MinFontSize when nothing fits is deliberate. The floor is where text stops being
    // readable, and going under it to win back a few pixels trades one unreadable result for
    // another; a band that overflows a block drawn too tight is at least visibly that.
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
