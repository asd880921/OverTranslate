using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// 標記: the marks the user draws over the capture, and the gesture that makes them.
/// </summary>
/// <remarks>
/// <para>This lives on the overlay rather than on the capture window for two reasons. The overlay is
/// already pinned to the whole virtual desktop and never moves, so it is a fixed frame of reference
/// for coordinates that must outlive the selection being dragged around. And it sits above the
/// translation bubbles, which is where a mark has to be: someone annotating after a translation is
/// marking up what is on screen, and after a translation half of what is on screen is bubbles.</para>
///
/// <para>It also means 截圖 picks the marks up for nothing. That copy renders this window's whole
/// content and crops it to the selection — see <c>RenderBubblesForSelection</c> — so a mark is in the
/// picture for the same reason a bubble is.</para>
/// </remarks>
public partial class OverlayWindow
{
    /// <summary>How far the pointer must travel before another point is recorded.</summary>
    /// <remarks>
    /// Mouse moves arrive far denser than a stroke needs. Without this a slow, deliberate line —
    /// which is what someone drawing carefully produces — lands hundreds of points inside a few DIP,
    /// and every one of them is re-rendered and re-hit-tested for the rest of the session.
    /// </remarks>
    private const double MinPointDistance = 1.2;

    /// <summary>
    /// How many steps back 復原 can go.
    /// </summary>
    /// <remarks>
    /// The history is a list of whole stroke lists rather than a log of edits, because a stroke is
    /// immutable once committed: a snapshot is a copy of the list, which is a handful of references.
    /// That makes undo and redo two integer moves and removes the entire class of bug where an
    /// inverse operation does not quite invert.
    /// </remarks>
    private const int HistoryLimit = 60;

    private readonly List<List<AnnotationStroke>> _annotationHistory = [[]];
    private int _annotationHistoryIndex;

    private bool _isAnnotating;
    private AnnotationTool _annotationTool = AnnotationTool.Pen;
    private Color _annotationColor = Colors.Orange;
    private double _annotationThickness = 4;
    private double _annotationOpacity = 0.45;

    private List<Point>? _wetPoints;
    private Polyline? _wetLine;

    // The strokes as they stood when an erase drag began, the path the eraser has taken since, and
    // what that leaves. The first is kept so the drag can be recomputed from scratch on every step:
    // subtracting the whole sweep so far from the untouched strokes gives the same answer however
    // the pointer got there, where subtracting each new circle from the previous result would pile
    // one geometry operation on another for the length of the drag.
    private List<AnnotationStroke>? _eraseBase;
    private List<Point>? _erasePoints;
    private List<AnnotationStroke>? _eraseWorkingSet;

    private Rect _annotationBounds;
    private Rect _annotationBoundsPhys;

    /// <summary>Where the pointer was last seen over the surface, for the eraser's ring to sit on.</summary>
    private Point? _lastPointer;

    /// <summary>Raised whenever the marks, or what can be undone, changed.</summary>
    public event EventHandler? AnnotationsChanged;

    public bool CanUndoAnnotation => _annotationHistoryIndex > 0;
    public bool CanRedoAnnotation => _annotationHistoryIndex < _annotationHistory.Count - 1;

    private List<AnnotationStroke> CurrentStrokes => _annotationHistory[_annotationHistoryIndex];

    /// <summary>
    /// Places the box the marks are confined to, in physical desktop pixels.
    /// </summary>
    /// <remarks>
    /// Called again every time the user moves or resizes the selection. Only the clip and the input
    /// surface move: the strokes stay exactly where they were drawn, so a box dragged off them hides
    /// them and a box dragged back shows them again. Clipping is reversible and deleting is not,
    /// which is the whole reason the marks are not owned by the box.
    /// </remarks>
    public void SetAnnotationBounds(double selPhysLeft, double selPhysTop, double selPhysWidth, double selPhysHeight)
    {
        _annotationBoundsPhys = new Rect(selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight);
        ApplyAnnotationBounds();
    }

    /// <summary>
    /// Converts the remembered box into this window's own coordinates and places the clip on it.
    /// </summary>
    /// <remarks>
    /// Split from the setter and run again from Loaded because the conversion needs the window's DPI,
    /// and that is only read once the window has a presentation source. The selection is known before
    /// then — the capture window has it the instant the drag ends — so the first call would otherwise
    /// divide by a placeholder scale of 1 and put the box somewhere else entirely on any monitor that
    /// is not at 100%.
    /// </remarks>
    private void ApplyAnnotationBounds()
    {
        var (selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight) =
            (_annotationBoundsPhys.X, _annotationBoundsPhys.Y,
             _annotationBoundsPhys.Width, _annotationBoundsPhys.Height);
        if (selPhysWidth <= 0 || selPhysHeight <= 0) return;

        _annotationBounds = new Rect(
            (selPhysLeft - _physBounds.Left) / _dpiX,
            (selPhysTop  - _physBounds.Top)  / _dpiY,
            Math.Max(1, selPhysWidth  / _dpiX),
            Math.Max(1, selPhysHeight / _dpiY));

        AnnotationCanvas.Clip = new RectangleGeometry(_annotationBounds);
        AnnotationCursorCanvas.Clip = new RectangleGeometry(_annotationBounds);

        Canvas.SetLeft(AnnotationSurface, _annotationBounds.X);
        Canvas.SetTop(AnnotationSurface,  _annotationBounds.Y);
        AnnotationSurface.Width  = _annotationBounds.Width;
        AnnotationSurface.Height = _annotationBounds.Height;
    }

    /// <summary>Hands the pointer to the pen and shows the drawing surface.</summary>
    /// <remarks>
    /// <para>The window is click-through at the Win32 level for the rest of its life, which is what
    /// lets the user carry on with whatever is underneath while a translation sits on top of it.
    /// Taking that away is what makes drawing possible at all.</para>
    ///
    /// <para>Only over the selection, though. Everything else in this window is either
    /// un-hittable or painted with a fully transparent brush, and a layered window is hit-tested
    /// against its composited alpha — so a click anywhere outside the box still finds nothing here
    /// and lands where it would have landed before. The box is the only thing 標記 takes.</para>
    /// </remarks>
    public void BeginAnnotating(AnnotationTool tool, Color color, double thickness, double opacity)
    {
        _annotationTool      = tool;
        _annotationColor     = color;
        _annotationThickness = thickness;
        _annotationOpacity   = opacity;

        if (_isAnnotating) return;
        _isAnnotating = true;

        WindowStyles.SetClickThrough(this, false);
        IsHitTestVisible = true;
        AnnotationSurface.Visibility = Visibility.Visible;
        ApplyAnnotationCursor();
    }

    /// <summary>Gives the pointer back to whatever is underneath. The marks stay on screen.</summary>
    public void EndAnnotating()
    {
        if (!_isAnnotating) return;
        _isAnnotating = false;

        AbandonWetStroke();
        _lastPointer = null;
        HideEraserRing();
        AnnotationSurface.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        WindowStyles.SetClickThrough(this, true);
    }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        _annotationTool = tool;
        ApplyAnnotationCursor();
    }

    public void SetAnnotationColor(Color color) => _annotationColor = color;

    public void SetAnnotationThickness(double thickness) => _annotationThickness = thickness;

    /// <summary>How see-through the next highlight will be. Ignored by the other two tools.</summary>
    public void SetAnnotationOpacity(double opacity) => _annotationOpacity = opacity;

    /// <summary>Half the width the 大小 slider is showing, which is what the ring is drawn at.</summary>
    /// <remarks>
    /// The slider gives a diameter because that is what "大小" means for a circle you can see, and
    /// because it is then the same kind of number as 粗細 — how wide the mark is. Everything inside
    /// works in radius.
    /// </remarks>
    private double EraserRadius => Math.Max(1, _annotationThickness / 2);

    /// <summary>The opacity a stroke made right now would carry.</summary>
    /// <remarks>
    /// Only the highlighter is ever see-through. A pen the user had made translucent would be a pen
    /// that no longer covers what it is drawn over, which is the one thing separating the two tools.
    /// </remarks>
    private double CurrentStrokeOpacity =>
        _annotationTool == AnnotationTool.Highlighter ? _annotationOpacity : 1.0;

    public void UndoAnnotation()
    {
        if (!CanUndoAnnotation) return;
        _annotationHistoryIndex--;
        RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RedoAnnotation()
    {
        if (!CanRedoAnnotation) return;
        _annotationHistoryIndex++;
        RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the right pointer in the user's hand, and the ring on screen when the eraser is in it.
    /// </summary>
    /// <remarks>
    /// The system pointer is taken away entirely for the eraser rather than left inside the ring.
    /// The ring is the pointer — it is drawn at the exact size of what it will remove, which an
    /// arrow or a crosshair beside it can only contradict.
    /// </remarks>
    private void ApplyAnnotationCursor()
    {
        bool erasing = _annotationTool == AnnotationTool.Eraser;
        AnnotationSurface.Cursor = erasing ? Cursors.None : Cursors.Pen;
        if (!erasing) HideEraserRing();
        else RenderEraserRing();
    }

    private void RenderEraserRing()
    {
        if (!_isAnnotating || _annotationTool != AnnotationTool.Eraser || _lastPointer is not { } point)
        {
            HideEraserRing();
            return;
        }

        double radius = EraserRadius;
        EraserRing.Width  = radius * 2;
        EraserRing.Height = radius * 2;
        Canvas.SetLeft(EraserRing, point.X - radius);
        Canvas.SetTop(EraserRing,  point.Y - radius);
        EraserRing.Visibility = Visibility.Visible;
    }

    private void HideEraserRing() => EraserRing.Visibility = Visibility.Collapsed;

    private void AnnotationSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isAnnotating) return;
        AnnotationSurface.CaptureMouse();
        var point = e.GetPosition(AnnotationCanvas);

        if (_annotationTool == AnnotationTool.Eraser)
        {
            _eraseBase   = [.. CurrentStrokes];
            _erasePoints = [point];
            EraseAlongPath();
            return;
        }

        _wetPoints = [point];
        _wetLine = BuildStrokeLine(new AnnotationStroke
        {
            Tool      = _annotationTool,
            Color     = _annotationColor,
            Thickness = _annotationThickness,
            Opacity   = CurrentStrokeOpacity,
            Points    = _wetPoints,
        });
        AnnotationCanvas.Children.Add(_wetLine);
    }

    private void AnnotationSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isAnnotating) return;

        var point = e.GetPosition(AnnotationCanvas);

        // Tracked on every move, button or no button: the ring has to follow the pointer before the
        // user commits to rubbing anything out. That is the whole point of it.
        _lastPointer = point;
        if (_annotationTool == AnnotationTool.Eraser) RenderEraserRing();

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_erasePoints is not null)
        {
            if (FarEnough(_erasePoints[^1], point))
            {
                _erasePoints.Add(point);
                EraseAlongPath();
            }
            return;
        }

        if (_wetPoints is null || _wetLine is null) return;
        if (!FarEnough(_wetPoints[^1], point)) return;

        _wetPoints.Add(point);
        _wetLine.Points.Add(point);
    }

    private void AnnotationSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        // Only once the drag is over. Running the eraser off the edge of the box and back in is one
        // continuous rub, and a ring that vanished halfway through it would read as the tool having
        // been dropped.
        if (AnnotationSurface.IsMouseCaptured) return;
        _lastPointer = null;
        HideEraserRing();
    }

    private static bool FarEnough(Point last, Point next)
    {
        double dx = next.X - last.X, dy = next.Y - last.Y;
        return dx * dx + dy * dy >= MinPointDistance * MinPointDistance;
    }

    private void AnnotationSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isAnnotating) return;
        AnnotationSurface.ReleaseMouseCapture();

        if (_erasePoints is not null)
        {
            var erased = _eraseWorkingSet ?? CurrentStrokes;

            // Anything the eraser took the last of goes now rather than staying as an empty shape
            // nothing can see and every later erase still has to work through.
            erased = [.. erased.Where(stroke => !stroke.IsErasedAway)];

            _eraseBase = null;
            _erasePoints = null;
            _eraseWorkingSet = null;

            // Nothing changed if the drag never touched a mark — and a history entry for that would
            // be a press of 復原 that visibly does nothing.
            if (!ReferenceEquals(erased, CurrentStrokes) && !SameStrokes(erased, CurrentStrokes))
                CommitStrokes(erased);
            else
                RedrawAnnotations();
            return;
        }

        if (_wetPoints is null) return;

        var points = _wetPoints;
        AbandonWetStroke();

        CommitStrokes([.. CurrentStrokes, new AnnotationStroke
        {
            Tool      = _annotationTool,
            Color     = _annotationColor,
            Thickness = _annotationThickness,
            Opacity   = CurrentStrokeOpacity,
            Points    = points,
        }]);
    }

    /// <summary>
    /// Drops whatever is half-drawn without committing it.
    /// </summary>
    /// <remarks>
    /// The line being drawn is a child of the ink canvas like any other, so leaving it there and
    /// redrawing from the committed list would show it until the next redraw and then lose it. It is
    /// removed rather than committed because the two callers are 標記 being switched off mid-stroke
    /// and the session ending, and neither is the user finishing a mark.
    /// </remarks>
    private void AbandonWetStroke()
    {
        if (_wetLine is not null) AnnotationCanvas.Children.Remove(_wetLine);
        _wetLine = null;
        _wetPoints = null;
        _eraseBase = null;
        _erasePoints = null;
        _eraseWorkingSet = null;
        if (AnnotationSurface.IsMouseCaptured) AnnotationSurface.ReleaseMouseCapture();
    }

    /// <summary>
    /// Takes everything the eraser has swept so far out of the strokes it began the drag with.
    /// </summary>
    /// <remarks>
    /// Recomputed from the drag's starting point every time rather than chipping away at the last
    /// result. Both give the same picture, but this one keeps each stroke's erased area a single
    /// union of one sweep instead of a stack of hundreds, so the geometry stays as simple at the end
    /// of a long rub as it was at the start.
    ///
    /// Painted as it goes, not at the end: an eraser that only shows what it took once the button
    /// comes up is an eraser nobody can aim.
    /// </remarks>
    private void EraseAlongPath()
    {
        if (_eraseBase is null || _erasePoints is null) return;

        double radius = EraserRadius;
        var sweep = AnnotationStroke.SweptCircle(_erasePoints, radius);
        var reach = sweep.Bounds;

        _eraseWorkingSet = [.. _eraseBase.Select(stroke =>
            reach.IntersectsWith(stroke.Outline.Bounds) && TouchesPath(stroke, radius)
                ? stroke.WithErased(sweep)
                : stroke)];

        RenderStrokes(_eraseWorkingSet);
    }

    /// <summary>Whether the eraser has passed close enough to this stroke to be worth the geometry.</summary>
    private bool TouchesPath(AnnotationStroke stroke, double radius) =>
        _erasePoints is not null && _erasePoints.Any(point => stroke.IsWithin(point, radius));

    private static bool SameStrokes(IReadOnlyList<AnnotationStroke> a, IReadOnlyList<AnnotationStroke> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    private void CommitStrokes(List<AnnotationStroke> next)
    {
        // Anything that was undone and not redone is gone the moment a new mark is made, which is
        // what every editor does and what the user means by carrying on from here.
        if (_annotationHistoryIndex < _annotationHistory.Count - 1)
            _annotationHistory.RemoveRange(
                _annotationHistoryIndex + 1, _annotationHistory.Count - _annotationHistoryIndex - 1);

        _annotationHistory.Add(next);
        if (_annotationHistory.Count > HistoryLimit) _annotationHistory.RemoveAt(0);
        _annotationHistoryIndex = _annotationHistory.Count - 1;

        RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RedrawAnnotations() => RenderStrokes(CurrentStrokes);

    private void RenderStrokes(IReadOnlyList<AnnotationStroke> strokes)
    {
        AnnotationCanvas.Children.Clear();
        foreach (var stroke in strokes)
            AnnotationCanvas.Children.Add(BuildStrokeVisual(stroke));
    }

    /// <summary>
    /// Draws a stroke the cheapest way that is still right for it.
    /// </summary>
    /// <remarks>
    /// A stroke nothing has been rubbed out of is a line, and a line is what WPF draws fastest and
    /// smoothest — the width and the caps are properties of the pen, and there is no outline to
    /// build. Once the eraser has taken a bite there is no pen that describes the result, so from
    /// then on it is a filled shape.
    /// </remarks>
    private static UIElement BuildStrokeVisual(AnnotationStroke stroke)
    {
        if (stroke.Erased is null) return BuildStrokeLine(stroke);

        return new Path
        {
            Fill             = new SolidColorBrush(stroke.Color),
            Data             = stroke.Painted,
            Opacity          = stroke.Opacity,
            IsHitTestVisible = false,
        };
    }

    private static Polyline BuildStrokeLine(AnnotationStroke stroke)
    {
        // Flat ends for the highlighter, round for the pen: a chisel tip is what makes a highlight
        // look laid down over a line of text rather than piped onto it.
        var cap = stroke.Tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;

        var line = new Polyline
        {
            Stroke             = new SolidColorBrush(stroke.Color),
            StrokeThickness    = stroke.Thickness,
            StrokeLineJoin     = PenLineJoin.Round,
            StrokeStartLineCap = cap,
            StrokeEndLineCap   = cap,
            Opacity            = stroke.Opacity,
            IsHitTestVisible   = false,
        };

        foreach (var point in stroke.Points) line.Points.Add(point);

        // A tap is one point, and a polyline through one point draws nothing. Repeating it gives the
        // round cap something to sit on, so a tap leaves the dot the user expected.
        if (line.Points.Count == 1) line.Points.Add(line.Points[0]);

        return line;
    }
}
