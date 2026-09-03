using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace OverTranslate.Models;

/// <summary>What a drag inside the selection does while 標記 is on.</summary>
public enum AnnotationTool
{
    /// <summary>An opaque line at the chosen colour and width.</summary>
    Pen,

    /// <summary>A wide translucent band, so the content under it still reads.</summary>
    Highlighter,

    /// <summary>Rubs out whatever its circle passes over.</summary>
    Eraser,
}

/// <summary>
/// One mark the user drew, in the overlay window's own DIP coordinates.
/// </summary>
/// <remarks>
/// <para>Deliberately not stored relative to the selection. The marks annotate what is on the frozen
/// screen, and the selection is a window onto them: moving or resizing the box changes which of them
/// show and which are clipped away, and never destroys one. A stroke kept relative to the box would
/// travel with it and end up sitting over content it was never drawn on.</para>
///
/// <para>The overlay window is pinned to the whole virtual desktop for the life of a session and
/// never moves, so its DIP space is a fixed frame of reference — the same one the bubbles are placed
/// in. Physical pixels would do as well and would have to be converted twice per point.</para>
///
/// <para>Immutable once committed, which is what lets the undo history be a list of snapshots: a
/// snapshot is a copy of the stroke list, and copying the list is enough because nothing can edit a
/// stroke afterwards. The eraser does not break that — it produces a new stroke rather than altering
/// one, which is also what makes a whole erase drag undo in a single press.</para>
/// </remarks>
public sealed class AnnotationStroke
{
    public required AnnotationTool Tool { get; init; }
    public required Color Color { get; init; }
    public required double Thickness { get; init; }
    public required IReadOnlyList<Point> Points { get; init; }

    /// <summary>
    /// How much of what is underneath still shows through. 1 for anything but a highlight.
    /// </summary>
    /// <remarks>
    /// Stored rather than worked out from the tool, because it is the user's to set: a highlighter
    /// laid over pale text and one laid over a screenshot of a dark game want different answers, and
    /// there is no value that is right for both.
    ///
    /// Applied to the rendered shape as a whole rather than to its brush. A translucent brush darkens
    /// wherever a stroke crosses itself — which for a hand-drawn highlight is everywhere it changes
    /// direction — and the result is a band with dark knots in it. Opacity on the element makes WPF
    /// compose the stroke once and then fade the result, so the band is even.
    /// </remarks>
    public required double Opacity { get; init; }

    /// <summary>
    /// What is left of the stroke after the eraser, or null while nothing has been taken.
    /// </summary>
    /// <remarks>
    /// <para>What survives, not what was removed. The difference matters at the speed a drag happens
    /// at. Keeping the removed area means every step subtracts a union that is one capsule longer
    /// than the last from an outline that never shrinks, so a step near the end of a long rub costs
    /// far more than one at the start — measured at 25ms a step, which is the lag. Keeping the
    /// survivor means each step cuts one small capsule out of a shape that is already reduced, so
    /// the work stops growing: the same 200-step rub measured 122ms in total against 3394ms.</para>
    ///
    /// <para>Those two figures were taken on one short stroke, and read as a claim about the level
    /// rather than the trend they are misleading. Six full-width scribbles measured 13ms a step and
    /// stayed there — flat, as the paragraph above says, but flat at a height that is felt. What
    /// sets that height is how many points the outline was built from, which is why the cut is
    /// taken against a simplified centre line — see <see cref="Simplified"/>.</para>
    ///
    /// <para>Either way the bite is the swept circle and not a cut across the centre line. Cutting
    /// the centre line is the cheap way to do this and it is wrong in exactly the case that matters
    /// most: a 30px highlight rubbed with a 12px eraser would lose either its whole width or nothing
    /// at all, because the centre line is a single thread down the middle of a band. Taking the
    /// circle out of the painted area makes what disappears exactly what the circle covered — which
    /// is what the ring under the pointer has been promising all along.</para>
    /// </remarks>
    public Geometry? Carved { get; init; }

    // Costly to produce and a pure function of the fields above, so it is worked out once and kept.
    // Safe on an immutable object, and the reason WithErased hands its outline to the stroke it
    // makes: the outline does not depend on what has been rubbed out, so re-widening a 400-point
    // line on every step of a drag would be work with an answer already known.
    private Geometry? _outline;

    /// <summary>The shape this stroke would paint if nothing had been erased from it.</summary>
    public Geometry Outline => _outline ??= BuildOutline();

    /// <summary>The shape it paints now.</summary>
    public Geometry Painted => Carved ?? Outline;

    /// <summary>Whether the eraser has taken all of it.</summary>
    public bool IsErasedAway => Painted.IsEmpty();

    /// <summary>The same stroke with <paramref name="sweep"/> rubbed out of it as well.</summary>
    public AnnotationStroke WithErased(Geometry sweep)
    {
        var next = new AnnotationStroke
        {
            Tool      = Tool,
            Color     = Color,
            Thickness = Thickness,
            Opacity   = Opacity,
            Points    = Points,
            Carved    = Freeze(Geometry.Combine(Painted, sweep, GeometryCombineMode.Exclude, null)),
        };

        next._outline = Outline;
        return next;
    }

    /// <summary>
    /// Whether the stroke passes within <paramref name="radius"/> of <paramref name="point"/>.
    /// </summary>
    /// <remarks>
    /// A cheap first question, asked before any geometry is built: most strokes are nowhere near the
    /// eraser, and widening and subtracting for all of them on every step of a drag would be work
    /// thrown away. It only has to be right in one direction — whatever it says yes to is then given
    /// the exact treatment.
    ///
    /// Measured against the segments, not just the recorded points: those are sampled from pointer
    /// moves, so a fast stroke is a handful of points with long gaps between them, and testing the
    /// points alone would miss most of a quick line.
    /// </remarks>
    public bool IsWithin(Point point, double radius)
    {
        double reach = radius + Thickness / 2;
        double reachSq = reach * reach;

        if (Points.Count == 1)
            return DistanceSquared(Points[0], point) <= reachSq;

        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegmentSquared(point, Points[i - 1], Points[i]) <= reachSq)
                return true;

        return false;
    }

    private Geometry BuildOutline()
    {
        // A tap is one point, and a line through one point has no length to widen along. The dot the
        // user expects is the shape of the nib itself.
        if (Points.Count < 2)
            return Freeze(new EllipseGeometry(Points[0], Thickness / 2, Thickness / 2));

        // Flat ends for the highlighter, round for the pen: a chisel tip is what makes a highlight
        // look laid down over a line of text rather than piped onto it.
        var cap = Tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;
        var pen = new Pen(Brushes.Black, Thickness)
        {
            StartLineCap = cap,
            EndLineCap   = cap,
            LineJoin     = PenLineJoin.Round,
        };

        return Freeze(CentreLine(Simplified(Points)).GetWidenedPathGeometry(pen));
    }

    /// <summary>How far a recorded point may sit off the line through its neighbours and still be dropped.</summary>
    /// <remarks>
    /// A tenth of a DIP: below anything that can be drawn, so the shape this produces is the shape
    /// the dense run of points described. It is a threshold on error, not on spacing, which is why
    /// it can be this small and still throw most of the points away — see <see cref="Simplified"/>.
    /// </remarks>
    private const double SimplifyTolerance = 0.1;

    /// <summary>The same line through fewer points, none of them further than the tolerance off it.</summary>
    /// <remarks>
    /// <para>Points arrive every 1.2 DIP of travel, which is what a smooth line on screen needs and
    /// far more than the shape needs: a hand moving in anything but a tight curl lays down long runs
    /// that are straight to well under a pixel. Widening keeps all of them — about five outline
    /// segments per recorded point — and every cut the eraser makes has to work through the whole
    /// outline however little of it the circle touches, so those runs are paid for on every step of
    /// every rub. Dropping them measured 5913 points to 663 and the cut from 9.1ms to 1.1ms, with
    /// the worst case tried (a dense curl, 16625 points) going 26.5ms to 2.9ms.</para>
    ///
    /// <para>Not <c>GetFlattenedPathGeometry</c>, which sounds like this and is not: flattening
    /// subdivides curves and never removes a point, and a widened polyline has no curves left to
    /// subdivide — measured at every tolerance from 0.1 to 2.0 it returned the same ~9940 segments.
    /// The cost is carried by the point count, so the point count is what has to come down.</para>
    ///
    /// <para>Applied here rather than to <see cref="Points"/> so it touches the erased shape only.
    /// Until the eraser takes a bite the stroke is drawn as a line through the recorded points and
    /// none of this is in the way of it; hit testing and undo keep the full run as drawn.</para>
    /// </remarks>
    private static IReadOnlyList<Point> Simplified(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return points;

        // Douglas-Peucker. Keep the two ends, then keep whichever point between them lies furthest
        // off the chord if it is off by more than the tolerance, and ask the same of each half.
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;

        var pending = new Stack<(int From, int To)>();
        pending.Push((0, points.Count - 1));

        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();
            if (to <= from + 1) continue;

            double ax = points[from].X, ay = points[from].Y;
            double dx = points[to].X - ax, dy = points[to].Y - ay;
            double chord = Math.Sqrt(dx * dx + dy * dy);

            int furthest = -1;
            double worst = SimplifyTolerance;
            for (int i = from + 1; i < to; i++)
            {
                double ox = points[i].X - ax, oy = points[i].Y - ay;

                // A closed loop back to where it started has no chord to measure against, so the
                // distance from the shared end stands in for it.
                double off = chord < 1e-9
                    ? Math.Sqrt(ox * ox + oy * oy)
                    : Math.Abs(dy * ox - dx * oy) / chord;

                if (off > worst) { worst = off; furthest = i; }
            }

            if (furthest < 0) continue;

            keep[furthest] = true;
            pending.Push((from, furthest));
            pending.Push((furthest, to));
        }

        var kept = new List<Point>(points.Count);
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) kept.Add(points[i]);

        return kept;
    }

    /// <summary>The bare line through a run of points, with nothing widened onto it yet.</summary>
    public static PathGeometry CentreLine(IReadOnlyList<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        figure.Segments.Add(new PolyLineSegment([.. points.Skip(1)], true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Everything a circle of <paramref name="radius"/> covered on its way along <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// One shape for a whole drag rather than a circle per pointer event. The pointer is sampled, so
    /// a string of separate circles would leave gaps at any speed above a crawl. It is the same
    /// widening the strokes themselves use, which is why the swept area has round ends and rounded
    /// turns — it is a circle being dragged.
    /// </remarks>
    public static Geometry SweptCircle(IReadOnlyList<Point> path, double radius)
    {
        if (path.Count < 2)
            return Freeze(new EllipseGeometry(path[0], radius, radius));

        var pen = new Pen(Brushes.Black, radius * 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap   = PenLineCap.Round,
            LineJoin     = PenLineJoin.Round,
        };

        return Freeze(CentreLine(path).GetWidenedPathGeometry(pen));
    }

    private static T Freeze<T>(T geometry) where T : Geometry
    {
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceToSegmentSquared(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;

        // A segment whose ends coincide — two pointer events at the same spot — has no direction to
        // project onto, and the projection below would divide by zero.
        if (lenSq <= double.Epsilon) return DistanceSquared(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        return DistanceSquared(p, new Point(a.X + t * dx, a.Y + t * dy));
    }
}
