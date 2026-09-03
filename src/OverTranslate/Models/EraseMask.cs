using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace OverTranslate.Models;

/// <summary>
/// What is left of one stroke after the eraser, held as coverage over the stroke's own box.
/// </summary>
/// <remarks>
/// <para>A mask rather than a cut shape. Subtracting the eraser from the stroke's outline is the
/// obvious way to do this and it ties the cost of a rub to how much the user has drawn: the outline
/// of a full-width scribble carries about five segments for every point recorded, and every cut has
/// to work through all of them however little of it the circle covers — measured at 13ms a step for
/// six such scribbles, which is felt, and which gets worse the more there is on screen. Painting
/// coverage costs the area of the circle and nothing else: 0.044ms a step, the same however much has
/// been drawn and however long the rub goes on.</para>
///
/// <para>It is also what a drawing program does. Erasing by boolean subtraction is the rare choice —
/// Excalidraw and tldraw do not erase parts of a stroke at all, and the apps that do are compositing
/// the eraser over the ink rather than recomputing geometry.</para>
///
/// <para>Kept in device pixels, so the edge the eraser leaves is as fine as the screen allows rather
/// than as fine as the layout unit. One byte in four is used — the alpha; <see cref="OpacityMask"/>
/// reads nothing else — and the colour bytes are left white so the bitmap is legible in a debugger.</para>
/// </remarks>
public sealed class EraseMask
{
    private readonly byte[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly double _scale;
    private readonly WriteableBitmap _bitmap;

    /// <summary>The stroke box this covers, in the overlay's own DIP coordinates.</summary>
    public Rect Bounds { get; }

    private EraseMask(Rect bounds, double scale, byte[]? copyFrom = null)
    {
        Bounds  = bounds;
        _scale  = scale;
        _width  = Math.Max(1, (int)Math.Ceiling(bounds.Width  * scale));
        _height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

        _pixels = new byte[_width * _height * 4];
        if (copyFrom is not null) Array.Copy(copyFrom, _pixels, _pixels.Length);
        else Array.Fill(_pixels, (byte)255);

        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
    }

    /// <summary>A mask over <paramref name="bounds"/> with nothing erased from it yet.</summary>
    public static EraseMask Covering(Rect bounds, double scale) => new(bounds, scale);

    /// <summary>
    /// A private copy, so that rubbing at what is on screen cannot reach into the undo history.
    /// </summary>
    /// <remarks>
    /// Taken once per stroke per drag rather than once per step: the drag works on its own copies
    /// throughout and hands them over when the button comes up, which is what makes a whole rub one
    /// press of 復原 — the same bargain the stroke list itself makes.
    /// </remarks>
    public EraseMask Copy() => new(Bounds, _scale, _pixels);

    /// <summary>
    /// Rubs out the circle of <paramref name="radius"/> dragged from one point to the other.
    /// </summary>
    /// <returns>Whether this actually uncovered anything that was still showing.</returns>
    public bool Erase(Point from, Point to, double radius)
    {
        // Into the mask's own pixels. The stroke is drawn in DIP and the mask is at device
        // resolution, so both the offset and the scale have to come off before anything is measured.
        double x0 = (from.X - Bounds.X) * _scale, y0 = (from.Y - Bounds.Y) * _scale;
        double x1 = (to.X   - Bounds.X) * _scale, y1 = (to.Y   - Bounds.Y) * _scale;
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
                // Distance to the segment, which is what makes the bite a dragged circle rather than
                // a circle at each end with a gap between them at any speed above a crawl.
                double t = lengthSquared < 1e-9
                    ? 0
                    : Math.Clamp(((x - x0) * dx + (y - y0) * dy) / lengthSquared, 0, 1);
                double cx = x0 + t * dx, cy = y0 + t * dy;
                double distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

                // A one-pixel ramp at the rim. Without it the channel the eraser cuts has a stepped
                // edge that is plainly visible against a stroke several times wider than the steps.
                double covered = Math.Clamp(r + 0.5 - distance, 0, 1);
                if (covered <= 0) continue;

                int alpha = (int)(255 * (1 - covered));
                int offset = (y * _width + x) * 4 + 3;
                if (alpha >= _pixels[offset]) continue;

                _pixels[offset] = (byte)alpha;
                took = true;
            }
        }

        if (!took) return false;

        // Only the box that changed. The bitmap is the brush the stroke is wearing, so writing to it
        // is what the user sees — there is no second copy to keep in step.
        int w = maxX - minX + 1, h = maxY - minY + 1;
        _bitmap.WritePixels(
            new Int32Rect(minX, minY, w, h),
            _pixels,
            _width * 4,
            (minY * _width + minX) * 4);

        return true;
    }

    /// <summary>How much of the stroke still shows at this spot: 1 untouched, 0 rubbed away.</summary>
    /// <remarks>
    /// Outside the mask answers 1, because a mask only covers its own stroke and everywhere else is
    /// somewhere this stroke was never going to paint.
    /// </remarks>
    public double CoverageAt(Point dip)
    {
        int x = (int)((dip.X - Bounds.X) * _scale);
        int y = (int)((dip.Y - Bounds.Y) * _scale);
        if (x < 0 || y < 0 || x >= _width || y >= _height) return 1;

        return _pixels[(y * _width + x) * 4 + 3] / 255.0;
    }

    /// <summary>The brush that wears this mask, placed over the stroke it belongs to.</summary>
    /// <remarks>
    /// An absolute viewport rather than the default bounding-box mapping: a stroke's element box is
    /// WPF's business and is not promised to be the widened bounds this was built for, and a mask
    /// off by even a pixel shows up as a rim of ink surviving along every cut.
    /// </remarks>
    public ImageBrush ToBrush() => new(_bitmap)
    {
        ViewportUnits = BrushMappingMode.Absolute,
        Viewport      = Bounds,
        Stretch       = Stretch.Fill,
        TileMode      = TileMode.None,
    };
}
