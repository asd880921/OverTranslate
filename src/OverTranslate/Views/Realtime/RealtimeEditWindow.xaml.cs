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
    private const double BaseModeSegmentWidth = 62;

    /// <summary>Gap between the mode control's track and the selected pill inside it.</summary>
    private const double BaseModeInset = 2;

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
    private double _modeInset = BaseModeInset;

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
        _modeInset = BaseModeInset * _uiScale;
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
            bounds, mode, _handleSize, _removeSize, _modeSegmentWidth, _modeInset, _uiScale);

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
        double modeTop = bounds.Top - _removeSize - _removeGap;
        if (modeTop < 0) modeTop = Math.Min(bounds.Top + _removeGap, BlockCanvas.ActualHeight - _removeSize);

        // Kept whole rather than allowed to run off the side: this is the control that says what the
        // block is, and half of it off-screen says nothing.
        double modeLeft = Math.Clamp(
            bounds.Left, 0, Math.Max(0, BlockCanvas.ActualWidth - visual.ModeControl.TrackWidth));

        Canvas.SetLeft(visual.ModeControl, modeLeft);
        Canvas.SetTop(visual.ModeControl, Math.Max(0, modeTop));
    }

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
            double modeInset,
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

            ModeControl = new ModeSegments(mode, removeSize, modeSegmentWidth, modeInset, uiScale);
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
    /// The per-block mode control: both choices always on screen, the selected one filled in.
    /// </summary>
    /// <remarks>
    /// A two-state chip that flips when clicked would be smaller, and it was tried first. It reads
    /// badly for this job in two ways: a chip labelled only with its state reads equally well as a
    /// state and as an action, and the two readings are opposites; and with several blocks on screen
    /// there is no way to see that a choice exists at all, let alone what the other option is. Both
    /// segments being visible answers "what is this block?" and "what else could it be?" at a glance,
    /// and switching is one click rather than read-then-flip.
    ///
    /// Everything here is built in code rather than as a template because the whole edit layer is —
    /// see <see cref="BlockVisual"/> — and because the pill has to be animated by hand: the control
    /// floats over content the user is still watching, so it has to settle rather than jump.
    /// </remarks>
    private sealed class ModeSegments : Border
    {
        // Response, not duration, in the sense the motion is designed for: long enough to be seen
        // as one thing moving rather than two things swapping, short enough that a second click
        // never queues up behind it. Eased out with no overshoot — nothing was thrown here, a
        // button was pressed, and a bounce would be motion the gesture did not pay for.
        private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(260));

        // Feedback on press has to be immediate or the control feels dead, so this is short enough
        // to read as instant while still being a movement rather than a jump.
        private static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(100));

        private const double PressedScale = 0.97;

        private readonly TranslateTransform _pillOffset = new();
        private readonly ScaleTransform _pressScale = new(1, 1);
        private readonly Border[] _segments;
        private readonly TextBlock[] _labels;
        private readonly double _segmentWidth;

        // Which segment the pointer went down on, or -1. The click is committed on release and only
        // if the pointer is still over that segment, so a press the user thought better of can be
        // taken back by sliding off it — the same forgiveness every other button on the desktop has.
        private int _pressedSegment = -1;

        public ModeSegments(
            RealtimeBlockMode mode, double height, double segmentWidth, double inset, double uiScale)
        {
            Value = mode;
            _segmentWidth = segmentWidth;
            TrackWidth = segmentWidth * 2 + inset * 2;

            Width = TrackWidth;
            Height = height;
            CornerRadius = new CornerRadius(height / 2);
            Background = ModeTrack;
            BorderBrush = ModeTrackEdge;
            BorderThickness = new Thickness(0, 1 * uiScale, 0, 0);
            Effect = new DropShadowEffect
            {
                BlurRadius = 8 * uiScale, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black
            };

            // Scaled about its own centre so the press reads as the control being pushed, not as it
            // sliding towards a corner.
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _pressScale;

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
            _segments =
            [
                BuildSegment(_labels[0], segmentWidth, "整條字幕、對話框——文字大，讀取時會縮小"),
                BuildSegment(_labels[1], segmentWidth, "遊戲面板、物品說明——文字小，讀取時不縮小"),
            ];

            var row = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(inset, 0, inset, 0),
            };
            foreach (var segment in _segments) row.Children.Add(segment);

            // The pill goes in first so the labels sit on top of it; a label the pill slid over
            // would otherwise disappear underneath it halfway through the move.
            var stack = new Grid();
            stack.Children.Add(pill);
            stack.Children.Add(row);
            Child = stack;

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
                    SetPressed(true);
                };
                segment.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var commit = _pressedSegment == picked && segment.IsMouseOver;
                    _pressedSegment = -1;
                    segment.ReleaseMouseCapture();
                    SetPressed(false);
                    if (commit) Select(picked == 0 ? RealtimeBlockMode.Subtitle : RealtimeBlockMode.Panel);
                };

                // Dragged off and back on again while held: the press follows the pointer, so the
                // control keeps saying what releasing right now would do.
                segment.MouseEnter += (_, _) => { if (_pressedSegment == picked) SetPressed(true); };
                segment.MouseLeave += (_, _) => { if (_pressedSegment == picked) SetPressed(false); };
            }

            ApplySelection(animate: false);
        }

        /// <summary>Raised when the user picks the mode this block is not already on.</summary>
        public event EventHandler? SelectionChanged;

        public RealtimeBlockMode Value { get; private set; }

        /// <summary>Full width of the control, so the caller can keep all of it on screen.</summary>
        public double TrackWidth { get; }

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
        }

        private void SetPressed(bool pressed)
        {
            var target = pressed ? PressedScale : 1.0;
            Move(_pressScale, ScaleTransform.ScaleXProperty, target, PressDuration, animate: true);
            Move(_pressScale, ScaleTransform.ScaleYProperty, target, PressDuration, animate: true);
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

        private static TextBlock BuildLabel(string text, double uiScale) => new()
        {
            Text = text,
            FontSize = 11.0 * uiScale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Transparent rather than unset: a null background is not hit-testable, and the segment is
        // the thing being clicked.
        private static Border BuildSegment(TextBlock label, double width, string tip) => new()
        {
            Width = width,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = label,
        };
    }
}
