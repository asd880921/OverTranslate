using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// The stroke currently under the pointer, drawn a segment at a time.
/// </summary>
/// <remarks>
/// <para>Separate from the finished marks because a line being drawn is the one thing on the surface
/// that changes on every pointer event, and the cheap way to show it is to add what is new rather
/// than to describe the whole line again. A <c>Polyline</c> does the latter: its points are its
/// definition, so each one added rebuilds the geometry for all of them. Measured over one stroke
/// that is 0.29ms at the thousandth point and 0.76ms at the four thousandth — 2177ms of rebuilding
/// across a single long scribble, which is the drawing going gradually stiff that a user reports as
/// "it gets worse the longer I draw, and the next stroke is fine again". Appending a visual per
/// segment measured 0.001ms and stayed there.</para>
///
/// <para>This is what an ink stack calls dynamic rendering: the wet stroke is rendered incrementally
/// while it is being collected, and only becomes part of the static picture once it is finished.</para>
///
/// <para>The segments are drawn opaque and the whole layer is faded instead, which is what keeps a
/// 螢光筆 even. Drawing each segment at the stroke's own opacity would compose them one over another
/// and leave a darker knot everywhere the line crosses itself or merely turns — the same reason the
/// finished stroke carries its opacity on the element rather than in its brush.</para>
/// </remarks>
internal sealed class WetInkLayer : FrameworkElement
{
    private readonly VisualCollection _segments;
    private Pen? _pen;

    public WetInkLayer()
    {
        _segments = new VisualCollection(this);
        IsHitTestVisible = false;
    }

    /// <summary>Starts a stroke, throwing away anything left of the last one.</summary>
    public void Begin(Color color, double thickness, double opacity, PenLineCap cap)
    {
        _segments.Clear();

        _pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = cap,
            EndLineCap   = cap,
            LineJoin     = PenLineJoin.Round,
        };
        _pen.Freeze();

        Opacity = opacity;
    }

    /// <summary>Adds the piece of the line the pointer has just covered.</summary>
    public void Extend(Point from, Point to)
    {
        if (_pen is null) return;

        double nib = _pen.Thickness / 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // A tap has no length to draw along, and a zero-length line draws nothing however round
            // the cap is. The dot the user expects is the nib itself.
            if (from == to)
            {
                dc.DrawEllipse(_pen.Brush, null, from, nib, nib);
                _segments.Add(visual);
                return;
            }

            // The joint, filled by hand. The finished stroke is one line with a round join, and each
            // vertex there is a disc of the nib's width; here each piece of the line is drawn on its
            // own, so a 螢光筆 — which is flat-ended, because that is what a chisel tip looks like —
            // leaves a wedge of bare screen at the outside of every turn. That is a line that comes
            // out dashed while it is being drawn and whole the moment it is let go.
            dc.DrawEllipse(_pen.Brush, null, from, nib, nib);
            dc.DrawLine(_pen, from, to);
        }

        _segments.Add(visual);
    }

    /// <summary>Takes the stroke away, which is what happens once it has been committed.</summary>
    public void Clear()
    {
        _segments.Clear();
        _pen = null;
    }

    protected override int VisualChildrenCount => _segments.Count;

    protected override Visual GetVisualChild(int index) => _segments[index];
}
