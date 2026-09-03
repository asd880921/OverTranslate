using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// Every finished mark, kept as one picture rather than as one shape per stroke.
/// </summary>
/// <remarks>
/// <para>The reason is what it costs to have them on screen at all, which is separate from what it
/// costs to draw or rub one out. A finished stroke left as a <c>Polyline</c> is re-rasterised every
/// time anything overlapping it changes, and it cannot be skipped the way a small shape can, because
/// a scribble's box covers most of the screen. Measured with a ring moved over the ink the way the
/// eraser cursor is: an empty surface renders a frame in 6.1ms, ten scribbles in 11.6ms, and thirty
/// in 19.6ms with frames as long as 30ms — visible as a cursor that will not keep up, before any
/// erasing has been asked for. The same thirty baked into one bitmap render in 6.1ms, which is the
/// empty figure again. The cost of showing what has been drawn stops depending on how much it is.</para>
///
/// <para>Ink goes on and the eraser takes off, both straight into the pixels: source-over for one
/// and destination-out for the other, each costing the area it touches and nothing else. Both are a
/// line of arithmetic here because the bitmap is premultiplied — the colour channels already carry
/// their alpha, so fading a pixel out is multiplying all four of them by the same number, and
/// laying one over another is an add and a multiply. Getting that wrong is what leaves coloured
/// fringes along an erased edge, and it is only right if the format is not mixed up.</para>
///
/// <para>What is not here is undo. The picture cannot be stepped backwards — pixels do not remember
/// what they covered — so the marks are still kept as a list of strokes, and going back a step
/// repaints this from that list. See <c>Replay</c>.</para>
/// </remarks>
public sealed class InkSurface
{
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private double _scale = 1;
    private Rect _bounds;

    /// <summary>The element that shows the marks. Added to the ink canvas once and left there.</summary>
    public Image Element { get; } = new() { IsHitTestVisible = false };

    /// <summary>
    /// Makes the surface cover <paramref name="bounds"/>, throwing away what it held if it has to grow.
    /// </summary>
    /// <remarks>
    /// Sized to the window rather than to the selection, because a mark is not owned by the box it
    /// was drawn in: moving the box off a mark hides it and moving back shows it again, so the ink
    /// has to go on living outside whatever the selection is at the time. Allocated on the first
    /// mark and not before — most captures are never drawn on.
    /// </remarks>
    public void Ensure(Rect bounds, double scale)
    {
        int width  = Math.Max(1, (int)Math.Ceiling(bounds.Width  * scale));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        if (_bitmap is not null && width == _width && height == _height && Math.Abs(scale - _scale) < 1e-9)
            return;

        _bounds = bounds;
        _scale  = scale;
        _width  = width;
        _height = height;
        _pixels = new byte[width * height * 4];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);

        Element.Source = _bitmap;
        Element.Width  = bounds.Width;
        Element.Height = bounds.Height;
        Canvas.SetLeft(Element, bounds.X);
        Canvas.SetTop(Element, bounds.Y);
    }

    /// <summary>Wipes the picture, which is where repainting from the stroke list starts.</summary>
    public void Clear()
    {
        if (_pixels is null || _bitmap is null) return;

        Array.Clear(_pixels);
        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
    }

    /// <summary>Paints the marks in order, which is the only way an erased-then-drawn-over spot comes out right.</summary>
    /// <remarks>
    /// In runs rather than one at a time. Order only has to be kept where it changes the answer, and
    /// that is at a rub: marks laid down between two rubs cannot reach each other's pixels, so they
    /// can be rasterised together and blended in once. One at a time measured 22ms a mark — 860ms to
    /// undo a step with forty marks on screen and 2.8 seconds with a hundred and twenty, which is a
    /// keypress that looks like a hang. The work becomes one pass per rub instead of one per mark.
    /// </remarks>
    public void Replay(IEnumerable<AnnotationStroke> strokes)
    {
        Clear();

        var run = new List<AnnotationStroke>();
        foreach (var stroke in strokes)
        {
            if (stroke.Tool != AnnotationTool.Eraser)
            {
                run.Add(stroke);
                continue;
            }

            LayAll(run);
            run.Clear();
            Rub(stroke);
        }

        LayAll(run);
    }

    /// <summary>Lays one finished stroke down.</summary>
    public void Lay(AnnotationStroke stroke) => LayAll([stroke]);

    /// <summary>Lays down a run of strokes with nothing rubbed out between them.</summary>
    public void LayAll(IReadOnlyList<AnnotationStroke> strokes)
    {
        if (_pixels is null || _bitmap is null || strokes.Count == 0) return;

        var reach = strokes[0].Bounds;
        for (int i = 1; i < strokes.Count; i++) reach.Union(strokes[i].Bounds);

        var box = Clamp(reach);
        if (box.IsEmpty) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(_scale, _scale));
            dc.PushTransform(new TranslateTransform(-box.DipX, -box.DipY));

            foreach (var stroke in strokes)
            {
                var pen = new Pen(new SolidColorBrush(stroke.Color), stroke.Thickness)
                {
                    StartLineCap = Cap(stroke.Tool),
                    EndLineCap   = Cap(stroke.Tool),
                    LineJoin     = PenLineJoin.Round,
                };

                // Composed whole and then faded, never piece by piece at its own opacity: a 螢光筆
                // crosses itself at every turn, and laying the pieces one over another is what
                // leaves a band with dark knots in it. The fade is per stroke, so two separate
                // marks still darken where they cross, as two passes of a real highlighter do.
                dc.PushOpacity(Math.Clamp(stroke.Opacity, 0, 1));

                // A single point is a tap. A round nib leaves a dot; a chisel one has no width
                // across a point it never moved off, so there is nothing to lay down.
                if (stroke.Points.Count == 1)
                {
                    if (pen.StartLineCap == PenLineCap.Round)
                        dc.DrawEllipse(pen.Brush, null, stroke.Points[0], stroke.Thickness / 2, stroke.Thickness / 2);
                }
                else
                {
                    dc.DrawGeometry(null, pen,
                        Line(AnnotationStroke.WithoutEndJitter(stroke.Points, stroke.Thickness)));
                }

                dc.Pop();
            }

            dc.Pop();
            dc.Pop();
        }

        var patch = new RenderTargetBitmap(box.Width, box.Height, 96, 96, PixelFormats.Pbgra32);
        patch.Render(visual);

        var src = new byte[box.Width * box.Height * 4];
        patch.CopyPixels(src, box.Width * 4, 0);

        for (int y = 0; y < box.Height; y++)
        {
            int from = y * box.Width * 4;
            int into = ((box.Y + y) * _width + box.X) * 4;

            for (int x = 0; x < box.Width * 4; x += 4)
            {
                // Premultiplied source-over. Alpha is one of the four channels rather than a
                // special case, which is what keeps the colours and the coverage in step.
                int sa = src[from + x + 3];
                if (sa == 0) continue;

                int keep = 255 - sa;
                for (int c = 0; c < 4; c++)
                    _pixels[into + x + c] =
                        (byte)Math.Min(255, src[from + x + c] + _pixels[into + x + c] * keep / 255);
            }
        }

        _bitmap.WritePixels(
            new Int32Rect(box.X, box.Y, box.Width, box.Height),
            _pixels, _width * 4, (box.Y * _width + box.X) * 4);
    }

    /// <summary>Takes the whole path of one erase drag back off again.</summary>
    private void Rub(AnnotationStroke path)
    {
        double radius = path.Thickness / 2;
        for (int i = 1; i < path.Points.Count; i++) Erase(path.Points[i - 1], path.Points[i], radius);
        if (path.Points.Count == 1) Erase(path.Points[0], path.Points[0], radius);
    }

    /// <summary>
    /// Rubs out the circle of <paramref name="radius"/> dragged from one point to the other.
    /// </summary>
    /// <returns>Whether it uncovered anything that was showing.</returns>
    public bool Erase(Point from, Point to, double radius)
    {
        if (_pixels is null || _bitmap is null) return false;

        double x0 = (from.X - _bounds.X) * _scale, y0 = (from.Y - _bounds.Y) * _scale;
        double x1 = (to.X   - _bounds.X) * _scale, y1 = (to.Y   - _bounds.Y) * _scale;
        double r  = radius * _scale;

        int minX = (int)Math.Max(0, Math.Floor(Math.Min(x0, x1) - r - 1));
        int maxX = (int)Math.Min(_width  - 1, Math.Ceiling(Math.Max(x0, x1) + r + 1));
        int minY = (int)Math.Max(0, Math.Floor(Math.Min(y0, y1) - r - 1));
        int maxY = (int)Math.Min(_height - 1, Math.Ceiling(Math.Max(y0, y1) + r + 1));
        if (minX > maxX || minY > maxY) return false;

        double dx = x1 - x0, dy = y1 - y0;
        double lengthSquared = dx * dx + dy * dy;
        bool took = false;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                // Distance to the segment, not to its ends: the pointer is sampled, so a bite taken
                // at each end would leave the middle of a quick drag standing.
                double t = lengthSquared < 1e-9
                    ? 0
                    : Math.Clamp(((x - x0) * dx + (y - y0) * dy) / lengthSquared, 0, 1);
                double cx = x0 + t * dx, cy = y0 + t * dy;
                double distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

                // A one-pixel ramp at the rim, or the channel the eraser cuts has a stepped edge
                // that is plainly visible against a stroke several times wider than the steps.
                double covered = Math.Clamp(r + 0.5 - distance, 0, 1);
                if (covered <= 0) continue;

                int offset = (y * _width + x) * 4;
                if (_pixels[offset + 3] == 0) continue;

                // Destination-out, premultiplied: all four channels fade together.
                int keep = (int)(255 * (1 - covered));
                for (int c = 0; c < 4; c++) _pixels[offset + c] = (byte)(_pixels[offset + c] * keep / 255);

                took = true;
            }
        }

        if (!took) return false;

        int w = maxX - minX + 1, h = maxY - minY + 1;
        _bitmap.WritePixels(
            new Int32Rect(minX, minY, w, h),
            _pixels, _width * 4, (minY * _width + minX) * 4);

        return true;
    }

    /// <summary>How much ink is showing at this spot: 1 solid, 0 bare. What a test can ask.</summary>
    public double InkAt(Point dip)
    {
        if (_pixels is null) return 0;

        int x = (int)((dip.X - _bounds.X) * _scale);
        int y = (int)((dip.Y - _bounds.Y) * _scale);
        if (x < 0 || y < 0 || x >= _width || y >= _height) return 0;

        return _pixels[(y * _width + x) * 4 + 3] / 255.0;
    }

    private static PenLineCap Cap(AnnotationTool tool) =>
        tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;

    private static StreamGeometry Line(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo([.. points.Skip(1)], isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private readonly record struct Patch(int X, int Y, int Width, int Height, double DipX, double DipY)
    {
        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>The stroke's box in whole pixels of this surface, cut down to what the surface holds.</summary>
    private Patch Clamp(Rect dip)
    {
        int x = (int)Math.Floor((dip.X - _bounds.X) * _scale);
        int y = (int)Math.Floor((dip.Y - _bounds.Y) * _scale);
        int right  = (int)Math.Ceiling((dip.Right  - _bounds.X) * _scale);
        int bottom = (int)Math.Ceiling((dip.Bottom - _bounds.Y) * _scale);

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        right  = Math.Min(_width,  right);
        bottom = Math.Min(_height, bottom);

        return new Patch(
            x, y, right - x, bottom - y,
            _bounds.X + x / _scale, _bounds.Y + y / _scale);
    }
}
