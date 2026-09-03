using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

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

    /// <summary>What the eraser has taken out of it, or null while it is untouched.</summary>
    /// <remarks>
    /// Coverage over the stroke rather than a shape cut out of it — see <see cref="EraseMask"/> for
    /// why, and for what it costs. The stroke keeps its points either way, so a rubbed stroke is
    /// still the line the user drew and is still drawn as one.
    /// </remarks>
    public EraseMask? Mask { get; init; }

    /// <summary>The box the stroke paints inside, with the width of the nib allowed for.</summary>
    /// <remarks>
    /// Worked out from the points rather than by widening them. The widened outline answers this
    /// exactly and costs about five segments for every point recorded, and nothing here needs the
    /// exact answer: it places the mask and rejects strokes the eraser is nowhere near, and both
    /// only need a box that is certainly big enough.
    /// </remarks>
    public Rect Bounds
    {
        get
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            // Half a nib each way for the width, and one more DIP so the round caps and the mask's
            // own soft rim have somewhere to land.
            double pad = Thickness / 2 + 1;
            return new Rect(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
        }
    }

    /// <summary>The same stroke wearing a mask of its own, ready to be rubbed at.</summary>
    /// <remarks>
    /// A copy is taken rather than the mask being painted in place, because the stroke this came
    /// from is in the undo history and the drag must not reach into it. Once per stroke per drag —
    /// see <see cref="EraseMask.Copy"/>.
    /// </remarks>
    public AnnotationStroke WithOwnMask(double scale) => new()
    {
        Tool      = Tool,
        Color     = Color,
        Thickness = Thickness,
        Opacity   = Opacity,
        Points    = Points,
        Mask      = Mask?.Copy() ?? EraseMask.Covering(Bounds, scale),
    };


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
