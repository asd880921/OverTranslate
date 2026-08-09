using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Shape = System.Windows.Shapes.Rectangle;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Edit mode: a transparent layer over one screen on which the user draws, moves and resizes the
/// areas to watch. Interactive for as long as it exists — the click-through, drawing half of the
/// feature is <see cref="RealtimeBlockWindow"/>, and the two are never on screen together.
/// </summary>
/// <remarks>
/// The window never takes activation (WS_EX_NOACTIVATE). Dragging a block out over a running game
/// would otherwise pull the foreground away from it, which for a full-screen game means a mode
/// switch and a black screen. Not being activatable also costs it the keyboard, so Esc is handled by
/// the session's <see cref="GlobalEscapeHook"/> rather than here.
/// </remarks>
public partial class RealtimeEditWindow : Window
{
    // Base sizes, in the units of a 100%-scaled display. Everything that draws or measures uses the
    // scaled fields below instead: this window is pinned onto a screen WPF may not have laid it out
    // for, so on a mixed-DPI desktop its own render scale is not the scale the user is looking at.
    private const double BaseMinBlockWidth = 48;
    private const double BaseMinBlockHeight = 22;
    private const double BaseHandleSize = 12;
    private const double BaseRemoveSize = 22;
    private const double BaseRemoveGap = 6;

    /// <summary>
    /// Width of one segment of the mode control. Both segments are this wide whichever label is
    /// longer, because a segmented control whose halves change width as the selection moves reads
    /// as two buttons rather than as one control with two states.
    /// </summary>
    private const double BaseModeSegmentWidth = 82;

    /// <summary>
    /// Height of the mode control, deliberately larger than the remove button beside it.
    /// </summary>
    /// <remarks>
    /// The two are not peers. The remove button is a single glyph the user aims at and clicks; this
    /// carries two words that have to be read at a glance, off a surface floating over moving
    /// picture, before the user has decided anything. Matching the smaller of the two made it look
    /// like a second piece of window furniture rather than the question it is.
    /// </remarks>
    private const double BaseModeHeight = 30;

    /// <summary>Gap between the mode control's track and the selected pill inside it.</summary>
    private const double BaseModeInset = 2;

    private const double BaseModeFontSize = 13.5;

    /// <summary>
    /// Width of the guidance plate under the mode control, and with it how the sentence wraps.
    /// </summary>
    /// <remarks>
    /// Fixed rather than sized to whichever sentence is showing: the two modes' guidance is a
    /// different length, and a plate that resized as the selection moved would make choosing a mode
    /// look like it had rearranged the screen.
    ///
    /// The number is the one that puts each sentence on a single line at
    /// <see cref="BaseHintFontSize"/> — measured, not chosen: both are fifty characters and both
    /// lay out at 640 points, so this is that plus the padding and a little slack for a machine
    /// whose font metrics differ. One line rather than two because a sentence read in one sweep is
    /// read; the plate is wider for it but shorter, and it hangs over content the user is trying to
    /// see. Worth re-measuring if either sentence is ever edited — the text still wraps rather than
    /// clips if it outgrows this, so the failure is a taller plate and not a lost half-sentence.
    /// </remarks>
    private const double BaseHintWidth = 680;

    private const double BaseHintFontSize = 13;

    private static readonly SolidColorBrush FrameStroke = Freeze(Color.FromArgb(0xE6, 0x1E, 0x90, 0xD5));
    private static readonly SolidColorBrush FrameFill = Freeze(Color.FromArgb(0x1C, 0x99, 0xC8, 0xF0));
    private static readonly SolidColorBrush HandleFill = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RemoveForeground = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    // The mode control floats over whatever is playing underneath, so its own surface has to carry
    // the contrast: a near-opaque dark track, and a hairline along the top edge in place of the
    // light a real material would catch. Anything lighter stops being legible over a bright scene.
    private static readonly SolidColorBrush ModeTrack = Freeze(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush ModeTrackEdge = Freeze(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ModeIdleForeground = Freeze(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private readonly System.Drawing.Rectangle _physBounds;
    private readonly IReadOnlyList<RealtimeBlockPlacement> _initialBlocks;
    private readonly int _maxBlocks;
    private readonly List<BlockVisual> _blocks = [];

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    // Target monitor scale relative to this window's render scale — 1.0 on a uniform desktop.
    private double _uiScale = 1.0;
    private double _minBlockWidth = BaseMinBlockWidth;
    private double _minBlockHeight = BaseMinBlockHeight;
    private double _handleSize = BaseHandleSize;
    private double _removeSize = BaseRemoveSize;
    private double _removeGap = BaseRemoveGap;
    private double _modeSegmentWidth = BaseModeSegmentWidth;
    private double _modeHeight = BaseModeHeight;
    private double _modeInset = BaseModeInset;
    private double _hintWidth = BaseHintWidth;

    private Point _drawOrigin;
    private Shape? _drawPreview;

    public RealtimeEditWindow(
        System.Drawing.Rectangle physBounds,
        IReadOnlyList<RealtimeBlockPlacement> initialBlocks,
        int maxBlocks)
    {
        InitializeComponent();

        _physBounds = physBounds;
        _initialBlocks = initialBlocks;
        _maxBlocks = maxBlocks;

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }

            ApplyScreenScale();

            foreach (var block in _initialBlocks)
                AddBlock(ToCanvas(block.Bounds), block.Mode, notify: false);

            RaiseBlocksChanged();
        };
    }

    /// <summary>Raised whenever a block is added, removed, moved or resized.</summary>
    public event EventHandler? BlocksChanged;

    /// <summary>Raised when a drag is refused because the block limit is already reached.</summary>
    public event EventHandler? LimitReached;

    public int BlockCount => _blocks.Count;

    /// <summary>The current blocks in physical screen pixels, ready to be watched.</summary>
    public IReadOnlyList<RealtimeBlockPlacement> GetPhysicalBlocks() =>
        [.. _blocks.Select(block =>
            new RealtimeBlockPlacement(ToPhysical(block.Bounds), block.ModeControl.Value))];

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowStyles.ApplyNoActivate(this);

        // Before the DPI is read in Loaded: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);
    }

    /// <summary>
    /// Rescales the handles, the remove button and the minimum block size for the monitor this
    /// window is actually pinned to. The block rectangles themselves need no correction — they are
    /// converted through this window's own render scale, which is the one WPF lays out with.
    /// </summary>
    private void ApplyScreenScale()
    {
        double targetScale = ScreenGeometry.ScaleAt(
            _physBounds.Left + _physBounds.Width / 2,
            _physBounds.Top + _physBounds.Height / 2);

        _uiScale = targetScale / _dpiX;
        _minBlockWidth = BaseMinBlockWidth * _uiScale;
        _minBlockHeight = BaseMinBlockHeight * _uiScale;
        _handleSize = BaseHandleSize * _uiScale;
        _removeSize = BaseRemoveSize * _uiScale;
        _removeGap = BaseRemoveGap * _uiScale;
        _modeSegmentWidth = BaseModeSegmentWidth * _uiScale;
        _modeHeight = BaseModeHeight * _uiScale;
        _modeInset = BaseModeInset * _uiScale;
        _hintWidth = BaseHintWidth * _uiScale;
    }

    // ── Drawing a new block ──────────────────────────────────────────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_blocks.Count >= _maxBlocks)
        {
            // Refuse the drag rather than letting the user draw a block that will be thrown away on
            // release. The control bar says why.
            LimitReached?.Invoke(this, EventArgs.Empty);
            return;
        }

        _drawOrigin = e.GetPosition(BlockCanvas);
        BlockCanvas.CaptureMouse();

        // Feedback on press, not on release: the box is visibly being drawn from the first pixel.
        _drawPreview = new Shape
        {
            Stroke = FrameStroke,
            StrokeThickness = 2 * _uiScale,
            StrokeDashArray = [4, 3],
            Fill = FrameFill,
            RadiusX = 3 * _uiScale,
            RadiusY = 3 * _uiScale,
        };
        Canvas.SetLeft(_drawPreview, _drawOrigin.X);
        Canvas.SetTop(_drawPreview, _drawOrigin.Y);
        BlockCanvas.Children.Add(_drawPreview);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drawPreview is null) return;

        // A mouse-up can go missing — another window grabs the capture, the session is torn down
        // mid-drag, the button is released while the pointer is off the desktop. The cost of not
        // noticing is severe and easy to mistake for the bar being broken: a canvas that still holds
        // the capture owns the cursor and every click across the whole screen, so the crosshair
        // follows the pointer over the control bar and none of its buttons respond. The button state
        // on the next move is the one signal that is always available.
        if (e.LeftButton == MouseButtonState.Released)
        {
            AbandonDraw();
            return;
        }

        var box = NormalizeToCanvas(_drawOrigin, e.GetPosition(BlockCanvas));
        Canvas.SetLeft(_drawPreview, box.X);
        Canvas.SetTop(_drawPreview, box.Y);
        _drawPreview.Width = box.Width;
        _drawPreview.Height = box.Height;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawPreview is null) return;

        // Read the box before tearing the drag down: AbandonDraw drops the capture, which raises
        // LostMouseCapture and clears _drawPreview underneath us.
        var box = NormalizeToCanvas(_drawOrigin, e.GetPosition(BlockCanvas));
        AbandonDraw();

        // A click, or a slip of the hand, should not leave a useless sliver behind.
        if (box.Width < _minBlockWidth || box.Height < _minBlockHeight) return;

        // Subtitle is the default because it is what nearly every block is, and because it is the
        // cheaper mistake: the other mode's fraction is the first fallback either way, so a panel
        // left on 字幕 costs one extra inference rather than a block that reads nothing.
        AddBlock(box, RealtimeBlockMode.Subtitle, notify: true);
    }

    // Capture lost to something else entirely (an Alt+Tab, another window taking it). The drag is
    // over whether we like it or not, so drop the half-drawn box rather than leave it on the canvas.
    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e) => AbandonDraw();

    /// <summary>Ends the in-progress drag without creating a block, leaving no capture behind.</summary>
    private void AbandonDraw()
    {
        if (_drawPreview is not null)
        {
            BlockCanvas.Children.Remove(_drawPreview);
            _drawPreview = null;
        }

        // Re-entrant by design: this raises LostMouseCapture, which calls back in — harmless, since
        // the preview is already gone by then.
        if (BlockCanvas.IsMouseCaptured) BlockCanvas.ReleaseMouseCapture();
    }

    private Rect NormalizeToCanvas(Point a, Point b)
    {
        double x = Math.Max(0, Math.Min(a.X, b.X));
        double y = Math.Max(0, Math.Min(a.Y, b.Y));
        double right = Math.Min(BlockCanvas.ActualWidth, Math.Max(a.X, b.X));
        double bottom = Math.Min(BlockCanvas.ActualHeight, Math.Max(a.Y, b.Y));
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    // ── Blocks ───────────────────────────────────────────────────────────────────────────────────

    private void AddBlock(Rect bounds, RealtimeBlockMode mode, bool notify)
    {
        var visual = new BlockVisual(
            bounds, mode, _handleSize, _removeSize,
            _modeSegmentWidth, _modeHeight, _modeInset, _hintWidth, _removeGap, _uiScale);

        visual.Body.DragDelta += (_, e) => Move(visual, e.HorizontalChange, e.VerticalChange);
        visual.Remove.Click += (_, e) =>
        {
            e.Handled = true;   // must not fall through and start drawing a new block underneath
            RemoveBlock(visual);
        };
        visual.ModeControl.SelectionChanged += (_, _) => RaiseBlocksChanged();

        for (int corner = 0; corner < visual.Corners.Length; corner++)
        {
            int index = corner;
            visual.Corners[index].DragDelta += (_, e) => Resize(visual, index, e.HorizontalChange, e.VerticalChange);
        }

        _blocks.Add(visual);
        RebuildCanvas();
        Apply(visual);

        if (notify) RaiseBlocksChanged();
    }

    private void RemoveBlock(BlockVisual visual)
    {
        _blocks.Remove(visual);
        RebuildCanvas();
        RaiseBlocksChanged();
    }

    // Frames first, then every handle: a handle sitting on the edge between two overlapping blocks
    // has to stay grabbable whichever block was drawn last.
    private void RebuildCanvas()
    {
        BlockCanvas.Children.Clear();

        foreach (var block in _blocks)
            BlockCanvas.Children.Add(block.Body);

        foreach (var block in _blocks)
        {
            foreach (var corner in block.Corners)
                BlockCanvas.Children.Add(corner);
            BlockCanvas.Children.Add(block.Remove);
            BlockCanvas.Children.Add(block.ModeControl);
        }

        foreach (var block in _blocks)
            Apply(block);
    }

    private void Move(BlockVisual visual, double dx, double dy)
    {
        var bounds = visual.Bounds;
        // Clamped to the screen rather than rubber-banded: this rectangle is a capture area, and a
        // part of it hanging off the screen would be a region the loop can never read.
        double x = Math.Clamp(bounds.X + dx, 0, Math.Max(0, BlockCanvas.ActualWidth - bounds.Width));
        double y = Math.Clamp(bounds.Y + dy, 0, Math.Max(0, BlockCanvas.ActualHeight - bounds.Height));
        visual.Bounds = new Rect(x, y, bounds.Width, bounds.Height);
        Apply(visual);
        RaiseBlocksChanged();
    }

    // Corner order: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.
    private void Resize(BlockVisual visual, int corner, double dx, double dy)
    {
        var bounds = visual.Bounds;
        double left = bounds.Left;
        double top = bounds.Top;
        double right = bounds.Right;
        double bottom = bounds.Bottom;

        bool movesLeft = corner is 0 or 2;
        bool movesTop = corner is 0 or 1;

        if (movesLeft) left = Math.Clamp(left + dx, 0, right - _minBlockWidth);
        else right = Math.Clamp(right + dx, left + _minBlockWidth, BlockCanvas.ActualWidth);

        if (movesTop) top = Math.Clamp(top + dy, 0, bottom - _minBlockHeight);
        else bottom = Math.Clamp(bottom + dy, top + _minBlockHeight, BlockCanvas.ActualHeight);

        visual.Bounds = new Rect(left, top, right - left, bottom - top);
        Apply(visual);
        RaiseBlocksChanged();
    }

    private void Apply(BlockVisual visual)
    {
        var bounds = visual.Bounds;

        visual.Body.Width = bounds.Width;
        visual.Body.Height = bounds.Height;
        Canvas.SetLeft(visual.Body, bounds.X);
        Canvas.SetTop(visual.Body, bounds.Y);

        PlaceHandle(visual.Corners[0], bounds.Left, bounds.Top);
        PlaceHandle(visual.Corners[1], bounds.Right, bounds.Top);
        PlaceHandle(visual.Corners[2], bounds.Left, bounds.Bottom);
        PlaceHandle(visual.Corners[3], bounds.Right, bounds.Bottom);

        // Outside the top-right corner by preference, so it never covers the content being framed;
        // tucked inside when the block is against the screen edge and there is no room out there.
        double removeLeft = bounds.Right + _removeGap;
        if (removeLeft + _removeSize > BlockCanvas.ActualWidth)
            removeLeft = bounds.Right - _removeSize - _removeGap;
        Canvas.SetLeft(visual.Remove, removeLeft);
        Canvas.SetTop(visual.Remove, Math.Max(0, bounds.Top));

        // Above the block's top-left corner: it reads as a label on the block without covering the
        // content being framed, and it is the far corner from the remove button — the two are one
        // click apart otherwise, and one of them destroys the block. Tucked inside the top edge when
        // the block is against the top of the screen, which is where a subtitle strip often is.
        // Above the block by preference, below it when there is no room up there, and only inside it
        // as a last resort. The order matters more than it did when this was a single chip: the
        // guidance makes this tall enough that putting it inside covers a good part of what the user
        // is trying to frame, so anywhere outside the block beats anywhere inside it.
        var mode = visual.ModeControl;
        double modeTop = bounds.Top - mode.TotalHeight - _removeGap;
        if (modeTop < 0)
        {
            double below = bounds.Bottom + _removeGap;
            modeTop = below + mode.TotalHeight <= BlockCanvas.ActualHeight
                ? below
                : Math.Min(bounds.Top + _removeGap, BlockCanvas.ActualHeight - mode.TotalHeight);
        }

        // Kept whole rather than allowed to run off the side: this is the control that says what the
        // block is and how to draw it, and half of it off-screen says neither.
        double modeLeft = Math.Clamp(
            bounds.Left, 0, Math.Max(0, BlockCanvas.ActualWidth - mode.TotalWidth));

        // Snapped to whole device pixels, because this one carries text. The block itself is placed
        // wherever the pointer left it and is right to be; a plate of 13-point CJK starting half way
        // across a pixel is soft for the whole time it is on screen, and UseLayoutRounding inside the
        // control cannot correct an origin that is already on a half pixel.
        Canvas.SetLeft(visual.ModeControl, SnapToPixels(modeLeft, _dpiX));
        Canvas.SetTop(visual.ModeControl, SnapToPixels(Math.Max(0, modeTop), _dpiY));
    }

    // Canvas coordinates are this window's device-independent units; a device pixel is 1/dpi of one.
    private static double SnapToPixels(double position, double dpi) =>
        dpi > 0 ? Math.Round(position * dpi) / dpi : position;

    private void PlaceHandle(Thumb handle, double centreX, double centreY)
    {
        Canvas.SetLeft(handle, centreX - _handleSize / 2);
        Canvas.SetTop(handle, centreY - _handleSize / 2);
    }

    private void RaiseBlocksChanged() => BlocksChanged?.Invoke(this, EventArgs.Empty);

    // ── Coordinates ──────────────────────────────────────────────────────────────────────────────

    private Rect ToCanvas(System.Drawing.Rectangle physical) => new(
        (physical.Left - _physBounds.Left) / _dpiX,
        (physical.Top - _physBounds.Top) / _dpiY,
        physical.Width / _dpiX,
        physical.Height / _dpiY);

    private System.Drawing.Rectangle ToPhysical(Rect canvas) => new(
        (int)Math.Round(_physBounds.Left + canvas.X * _dpiX),
        (int)Math.Round(_physBounds.Top + canvas.Y * _dpiY),
        Math.Max(1, (int)Math.Round(canvas.Width * _dpiX)),
        Math.Max(1, (int)Math.Round(canvas.Height * _dpiY)));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The elements that make up one block on the canvas. They are separate children rather than a
    /// single composed control so the corner handles and the remove button are not clipped by — and
    /// do not have to be hit-tested through — the frame itself.
    /// </summary>
    private sealed class BlockVisual
    {
        private static readonly Cursor[] CornerCursors =
            [Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNESW, Cursors.SizeNWSE];

        public BlockVisual(
            Rect bounds,
            RealtimeBlockMode mode,
            double handleSize,
            double removeSize,
            double modeSegmentWidth,
            double modeHeight,
            double modeInset,
            double hintWidth,
            double gap,
            double uiScale)
        {
            Bounds = bounds;

            Body = new Thumb
            {
                Cursor = Cursors.SizeAll,
                Template = BuildFrameTemplate(uiScale),
            };

            Corners = [.. CornerCursors.Select(cursor => new Thumb
            {
                Width = handleSize,
                Height = handleSize,
                Cursor = cursor,
                Template = BuildHandleTemplate(handleSize, uiScale),
            })];

            Remove = new Button
            {
                Width = removeSize,
                Height = removeSize,
                Cursor = Cursors.Hand,
                ToolTip = "移除此區塊",
                Template = BuildRemoveTemplate(removeSize, uiScale),
            };

            ModeControl = new ModeSegments(
                mode, modeHeight, modeSegmentWidth, modeInset, hintWidth, gap, uiScale);
        }

        public Rect Bounds { get; set; }
        public Thumb Body { get; }
        public Thumb[] Corners { get; }
        public Button Remove { get; }

        /// <summary>What the user says this block holds — see <see cref="RealtimeBlockMode"/>.</summary>
        public ModeSegments ModeControl { get; }

        private static ControlTemplate BuildFrameTemplate(double uiScale)
        {
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BorderBrushProperty, FrameStroke);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(2 * uiScale));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(3 * uiScale));
            frame.SetValue(Border.BackgroundProperty, FrameFill);
            // The frame floats over unpredictable content — a shadow is what keeps the edge legible
            // over a bright scene as well as a dark one.
            frame.SetValue(UIElement.EffectProperty, new DropShadowEffect
            {
                BlurRadius = 10 * uiScale, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black
            });
            return new ControlTemplate(typeof(Thumb)) { VisualTree = frame };
        }

        private static ControlTemplate BuildHandleTemplate(double handleSize, double uiScale)
        {
            var handle = new FrameworkElementFactory(typeof(Border));
            handle.SetValue(Border.BackgroundProperty, HandleFill);
            handle.SetValue(Border.BorderBrushProperty, FrameStroke);
            handle.SetValue(Border.BorderThicknessProperty, new Thickness(2 * uiScale));
            handle.SetValue(Border.CornerRadiusProperty, new CornerRadius(handleSize / 2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = handle };
        }

        private static ControlTemplate BuildRemoveTemplate(double removeSize, double uiScale)
        {
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "✕");
            glyph.SetValue(TextBlock.FontSizeProperty, 11.0 * uiScale);
            glyph.SetValue(TextBlock.ForegroundProperty, RemoveForeground);
            glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, FrameStroke);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(removeSize / 2));
            chip.SetValue(UIElement.EffectProperty, new DropShadowEffect
            {
                BlurRadius = 8 * uiScale, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black
            });
            chip.AppendChild(glyph);

            return new ControlTemplate(typeof(Button)) { VisualTree = chip };
        }
    }


    /// <summary>
    /// The per-block mode control: both choices always on screen, the selected one filled in, and
    /// under it the guidance for drawing a block of that kind.
    /// </summary>
    /// <remarks>
    /// A two-state chip that flips when clicked would be smaller, and it was tried first. It reads
    /// badly for this job in two ways: a chip labelled only with its state reads equally well as a
    /// state and as an action, and the two readings are opposites; and with several blocks on screen
    /// there is no way to see that a choice exists at all, let alone what the other option is. Both
    /// segments being visible answers "what is this block?" and "what else could it be?" at a glance,
    /// and switching is one click rather than read-then-flip.
    ///
    /// The guidance sits under the control rather than beside it, both hard against the same left
    /// edge, so the pair reads as one column starting at the block's corner. It lives here rather
    /// than in the settings screen or a first-run tip because this is the one moment it can be acted
    /// on: the user is holding the block. Issue #35 measured what its two halves are worth — a
    /// subtitle framed against its own text lost 20 of 39 frames to the collapse filter, and one
    /// framed too narrowly reads "bury steak!" for "Salisbury steak!" — and neither is something the
    /// program can correct afterwards or the user can guess at.
    ///
    /// Everything here is built in code rather than as a template because the whole edit layer is —
    /// see <see cref="BlockVisual"/> — and because the pill has to be animated by hand: the control
    /// floats over content the user is still watching, so it has to settle rather than jump.
    ///
    /// ON THE TEXT LOOKING SOFT. This window is layered (<c>AllowsTransparency</c>), and WPF turns
    /// ClearType off for the whole of a layered window — every glyph here is greyscale antialiased
    /// and no setting changes that. What is left is worth doing and is done below: the drop shadows
    /// are drawn on their own layer rather than on an ancestor of the text, because an Effect pushes
    /// everything beneath it through an intermediate surface; the text is formatted in Display mode,
    /// which snaps stems to whole pixels at the small sizes used here; and the control rounds its own
    /// layout, while the canvas rounds the position it is placed at, so the first glyph starts on the
    /// pixel grid instead of half way across one — the same fix AboutOverlay's card carries, for the
    /// same reason.
    /// </remarks>
    private sealed class ModeSegments : StackPanel
    {
        // Response, not duration, in the sense the motion is designed for: long enough to be seen
        // as one thing moving rather than two things swapping, short enough that a second click
        // never queues up behind it. Eased out with no overshoot — nothing was thrown here, a
        // button was pressed, and a bounce would be motion the gesture did not pay for.
        private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(260));

        // The guidance does not move when the mode changes, it is replaced — so it crosses over
        // rather than sliding, and faster than the pill, because a sentence that is still fading
        // while the user starts reading it is worse than one that was simply there.
        private static readonly Duration HintFadeDuration = new(TimeSpan.FromMilliseconds(140));

        // Feedback on press has to be immediate or the control feels dead, so this is short enough
        // to read as instant while still being a movement rather than a jump.
        private static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(100));

        // Pressing lights the segment rather than shrinking the control, and that is a rendering
        // decision as much as a design one. A scale on the track is a transform over text, and WPF
        // drops pixel snapping the moment it believes text is animating, then ramps it back over
        // about a second — so every press would leave both labels soft for a beat afterwards (see
        // ShellWindow.AnimateContentIn, which pays a bitmap cache to avoid exactly that). Lighting
        // the segment also says which of the two is being pressed, which a scale of the whole
        // control cannot.
        private static readonly SolidColorBrush PressHighlight =
            Freeze(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

        /// <summary>
        /// One step up from regular, for the guidance — a paragraph of small text on a translucent
        /// surface over moving picture, which needs the weight to stay readable.
        /// </summary>
        /// <remarks>
        /// Deliberately not Medium, which is what "a little heavier" would normally mean. This text
        /// is Chinese and falls back to Microsoft JhengHei UI, which ships Regular and Bold and
        /// nothing between: measured on this sentence at 13pt, Medium renders identically to Regular
        /// (17.47% ink) because 500 matches back to the 400 face, while SemiBold renders identically
        /// to Bold (21.97%) because 600 matches forward to the 700 one. There is no half step to
        /// pick, and this is the same one the rest of the application uses to emphasise (see
        /// SectionHeader in SharedStyles).
        ///
        /// The two mode labels do NOT take it, and the same measurement is why. Weight buys
        /// readability by thickening strokes, and it costs the gaps between them — which is a good
        /// trade over a sentence of ordinary characters and a bad one over 字幕 and 遊戲介面, where
        /// 遊, 戲 and 幕 carry twelve to seventeen strokes each and close up into a blot at this
        /// size. Those two words are also not the ones needing help to be found: one sits in white
        /// on a saturated pill and the other is the only other thing on the track.
        /// </remarks>
        private static readonly FontWeight HeavierOverPicture = FontWeights.SemiBold;

        /// <summary>
        /// How to draw a block of each kind. Named for what the user does, not for what the
        /// recogniser then does with it: the reasons live in <see cref="RealtimeDetectorSize"/> and
        /// <see cref="CollapsedDetection"/>, and neither is something to explain over a paused game.
        /// </summary>
        private const string SubtitleHint =
            "框選時，上下約留半個至一個字的空間，避免貼齊文字；" +
            "左右可適度放寬，但不要框入過多空白範圍，避免雜訊。";

        private const string PanelHint =
            "框選時，需翻譯的區域請保留一些空間，避免貼齊內容；" +
            "若畫面有多個區域則建議分開框選，避免辨識速度過慢。";

        private readonly TranslateTransform _pillOffset = new();
        private readonly Border[] _segments;
        private readonly Border[] _highlights;
        private readonly TextBlock[] _labels;
        private readonly TextBlock[] _hints;
        private readonly double _segmentWidth;

        // Which segment the pointer went down on, or -1. The click is committed on release and only
        // if the pointer is still over that segment, so a press the user thought better of can be
        // taken back by sliding off it — the same forgiveness every other button on the desktop has.
        private int _pressedSegment = -1;

        public ModeSegments(
            RealtimeBlockMode mode,
            double height,
            double segmentWidth,
            double inset,
            double hintWidth,
            double gap,
            double uiScale)
        {
            Value = mode;
            _segmentWidth = segmentWidth;

            Orientation = System.Windows.Controls.Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Left;

            // Sizes here are base units times a monitor scale, so most of them land on fractions of
            // a pixel. Rounded, the plate edges and the text inside them start on whole pixels.
            UseLayoutRounding = true;

            var trackWidth = segmentWidth * 2 + inset * 2;
            var radius = height / 2;

            var pill = new Border
            {
                Width = segmentWidth,
                Height = height - inset * 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(inset, 0, 0, 0),
                CornerRadius = new CornerRadius((height - inset * 2) / 2),
                Background = FrameStroke,
                RenderTransform = _pillOffset,
            };

            _labels = [BuildLabel("字幕", uiScale), BuildLabel("遊戲介面", uiScale)];

            // Rounded on the outer end only, so a press on either half stays inside the capsule.
            _highlights =
            [
                BuildHighlight(new CornerRadius(radius, 0, 0, radius), inset),
                BuildHighlight(new CornerRadius(0, radius, radius, 0), inset),
            ];

            _segments =
            [
                BuildSegment(_labels[0], _highlights[0], segmentWidth, "整條字幕、對話框——文字大，讀取時會縮小"),
                BuildSegment(_labels[1], _highlights[1], segmentWidth, "遊戲面板、物品說明——文字小，讀取時不縮小"),
            ];

            var row = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(inset, 0, inset, 0),
            };
            foreach (var segment in _segments) row.Children.Add(segment);

            // The pill goes in first so the labels sit on top of it; a label the pill slid over
            // would otherwise disappear underneath it halfway through the move.
            var trackContent = new Grid();
            trackContent.Children.Add(pill);
            trackContent.Children.Add(row);

            var track = Plate(trackWidth, new CornerRadius(radius), trackContent, uiScale);
            track.HorizontalAlignment = HorizontalAlignment.Left;
            track.Height = height;

            _hints = [BuildHint(SubtitleHint, uiScale), BuildHint(PanelHint, uiScale)];

            // Both sentences are laid out at once and only one is opaque, so the plate is as tall as
            // the longer of them from the start and choosing a mode cannot change its size.
            var hintContent = new Grid
            {
                Margin = new Thickness(14 * uiScale, 8 * uiScale, 14 * uiScale, 8 * uiScale),
            };
            foreach (var hint in _hints) hintContent.Children.Add(hint);

            var hintPlate = Plate(hintWidth, new CornerRadius(8 * uiScale), hintContent, uiScale);
            hintPlate.Margin = new Thickness(0, gap, 0, 0);

            // Reads, never clicked. Left hit-testable it would swallow the drag that starts a new
            // block on the picture behind it, for no gain.
            hintPlate.IsHitTestVisible = false;

            Children.Add(track);
            Children.Add(hintPlate);

            for (var index = 0; index < _segments.Length; index++)
            {
                var segment = _segments[index];
                var picked = index;

                segment.MouseLeftButtonDown += (_, e) =>
                {
                    // Must not fall through to the canvas underneath, which would take this as the
                    // start of a new block being drawn.
                    e.Handled = true;
                    _pressedSegment = picked;
                    segment.CaptureMouse();
                    SetPressed(picked, true);
                };
                segment.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var commit = _pressedSegment == picked && segment.IsMouseOver;
                    _pressedSegment = -1;
                    segment.ReleaseMouseCapture();
                    SetPressed(picked, false);
                    if (commit) Select(picked == 0 ? RealtimeBlockMode.Subtitle : RealtimeBlockMode.Panel);
                };

                // Dragged off and back on again while held: the press follows the pointer, so the
                // control keeps saying what releasing right now would do.
                segment.MouseEnter += (_, _) => { if (_pressedSegment == picked) SetPressed(picked, true); };
                segment.MouseLeave += (_, _) => { if (_pressedSegment == picked) SetPressed(picked, false); };
            }

            ApplySelection(animate: false);

            // Measured up front because the canvas positions this by hand and needs its size before
            // layout has run — and it is a fixed size for the life of the block: the plate's width
            // is given and both sentences are already laid out inside it.
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            TotalWidth = DesiredSize.Width;
            TotalHeight = DesiredSize.Height;
        }

        /// <summary>Raised when the user picks the mode this block is not already on.</summary>
        public event EventHandler? SelectionChanged;

        public RealtimeBlockMode Value { get; private set; }

        /// <summary>Size of the control and its guidance, so the caller can keep all of it on screen.</summary>
        public double TotalWidth { get; }

        public double TotalHeight { get; }

        private void Select(RealtimeBlockMode mode)
        {
            if (mode == Value) return;

            Value = mode;
            ApplySelection(animate: true);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplySelection(bool animate)
        {
            var selected = Value == RealtimeBlockMode.Subtitle ? 0 : 1;

            for (var index = 0; index < _labels.Length; index++)
                _labels[index].Foreground = index == selected ? RemoveForeground : ModeIdleForeground;

            Move(_pillOffset, TranslateTransform.XProperty, selected * _segmentWidth, SlideDuration, animate);

            for (var index = 0; index < _hints.Length; index++)
                Fade(_hints[index], index == selected ? 1.0 : 0.0, HintFadeDuration, animate);
        }

        private void SetPressed(int segment, bool pressed) =>
            Fade(_highlights[segment], pressed ? 1.0 : 0.0, PressDuration, animate: true);

        /// <summary>
        /// A surface with its shadow on one layer and its contents on another.
        /// </summary>
        /// <remarks>
        /// The two layers exist only so the text is not a descendant of the Effect. An Effect renders
        /// its whole subtree into an intermediate surface first, and text that has been through one
        /// comes back softer than text drawn straight onto the window — which on a layered window,
        /// where ClearType is already unavailable, is the difference between legible and not. The
        /// shadow layer holds the fill and the effect and nothing else; the content layer holds the
        /// hairline and the children, and carries no effect at all.
        /// </remarks>
        private static Border Plate(double width, CornerRadius corner, UIElement content, double uiScale)
        {
            var shadow = new Border
            {
                CornerRadius = corner,
                Background = ModeTrack,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 8 * uiScale, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black
                },
            };

            var body = new Border
            {
                CornerRadius = corner,
                BorderBrush = ModeTrackEdge,
                BorderThickness = new Thickness(0, 1 * uiScale, 0, 0),
                Child = content,
            };

            var layers = new Grid();
            layers.Children.Add(shadow);
            layers.Children.Add(body);

            return new Border { Width = width, Child = layers };
        }

        /// <summary>
        /// Animates one transform property to a new value, from wherever it is on screen right now.
        /// </summary>
        /// <remarks>
        /// The animation is given a target and no start, which is what makes a second click part way
        /// through the first one's movement continue from where the pill actually is rather than
        /// jumping back to where it started. Skipped entirely when the desktop has animation turned
        /// off, and then the property is cleared first — an animation left in place would otherwise
        /// hold the value it finished on and ignore everything set afterwards.
        /// </remarks>
        private static void Move(
            Transform transform, DependencyProperty property, double to, Duration duration, bool animate)
        {
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                transform.BeginAnimation(property, null);
                transform.SetValue(property, to);
                return;
            }

            transform.BeginAnimation(property, new DoubleAnimation(to, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        /// <summary>Cross-fades a layer, from its current opacity — see <see cref="Move"/>.</summary>
        private static void Fade(UIElement element, double to, Duration duration, bool animate)
        {
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                return;
            }

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private static TextBlock BuildLabel(string text, double uiScale) =>
            Sharpen(new TextBlock
            {
                Text = text,
                FontSize = BaseModeFontSize * uiScale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Leading a little looser than the default: this is dense CJK read once, off a translucent
        // surface, over moving picture, and the extra air is what stops the lines running together.
        private static TextBlock BuildHint(string text, double uiScale) =>
            Sharpen(new TextBlock
            {
                Text = text,
                FontSize = BaseHintFontSize * uiScale,
                FontWeight = HeavierOverPicture,
                LineHeight = BaseHintFontSize * 1.6 * uiScale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Foreground = RemoveForeground,
                VerticalAlignment = VerticalAlignment.Top,
            });

        // Display formatting rounds glyph widths and positions onto whole pixels, which at these
        // sizes is the difference between a stem one pixel wide and a stem smeared across two. WPF's
        // default (Ideal) keeps the typographic metrics instead, which is right for large text and
        // wrong for small interface text — and this is small interface text over moving picture.
        private static TextBlock Sharpen(TextBlock text)
        {
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.ClearType);
            return text;
        }

        private static Border BuildHighlight(CornerRadius corner, double inset) => new()
        {
            Background = PressHighlight,
            CornerRadius = corner,
            Margin = new Thickness(0, inset, 0, inset),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        // Transparent rather than unset: a null background is not hit-testable, and the segment is
        // the thing being clicked.
        private static Border BuildSegment(TextBlock label, Border highlight, double width, string tip)
        {
            var content = new Grid();
            content.Children.Add(highlight);
            content.Children.Add(label);

            return new Border
            {
                Width = width,
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = content,
            };
        }
    }
}
