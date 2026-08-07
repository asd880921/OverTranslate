using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// A coarse, noise-tolerant summary of a captured area — or of just the strips of it holding text —
/// used to decide whether anything worth recognising has changed since the last poll.
/// </summary>
/// <remarks>
/// This replaced an exact hash, which was the right shape and the wrong sensitivity. Real screen
/// content is never bit-identical between two frames: video carries compression noise, subpixel
/// antialiasing shifts as things move behind text, and a gradient dithers differently every repaint.
/// An exact hash calls all of that a change, so recognition ran continuously over content whose
/// words had not moved at all — measured at an 85% duty cycle on one region, for nothing.
///
/// So the area is reduced to a grid of average brightness cells and compared with a tolerance twice
/// over: a cell has to shift by more than <see cref="CellTolerance"/> to count at all, and enough
/// cells have to do so before the frame counts as changed. Noise moves every cell a little and fails
/// the first test; a line of text changing moves a fifth of them a lot and passes both.
/// </remarks>
internal sealed class FrameFingerprint
{
    // Cell counts, not pixel sizes: an area is summarised at the same resolution whatever its size,
    // so the comparison thresholds below mean the same thing for a 200px strip and a 1200px one.
    private const int CellsX = 32;
    private const int TextBandCellsY = 8;
    private const int FullAreaCellsY = 16;

    // Every second pixel in each direction. The cell averages barely move for a finer step, and this
    // runs several times a second on every watched region.
    private const int SampleStep = 2;

    /// <summary>
    /// How far one cell's brightness (0–255) may drift before it counts as changed. Sized above
    /// compression noise and antialiasing, well below the contrast between glyphs and their
    /// background.
    /// </summary>
    private const int CellTolerance = 12;

    /// <summary>
    /// What share of cells must have changed before the frame has. A single cell over the tolerance
    /// is a glint or a cursor; a line of text changing takes a good fraction of the grid with it.
    /// </summary>
    private const int ChangedCellPercent = 5;

    private readonly byte[] _cells;

    internal FrameFingerprint(byte[] cells) => _cells = cells;

    /// <param name="areas">
    /// Sub-rectangles to summarise, in bitmap coordinates; null or empty summarises the whole bitmap.
    /// </param>
    public static FrameFingerprint Capture(Bitmap bitmap, IReadOnlyList<Rectangle>? areas)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var frame = new Rectangle(0, 0, data.Width, data.Height);

            if (areas is null || areas.Count == 0)
                return new FrameFingerprint(Summarise(data, frame, FullAreaCellsY));

            var cells = new List<byte>(areas.Count * CellsX * TextBandCellsY);
            foreach (var area in areas)
            {
                var clipped = Rectangle.Intersect(area, frame);
                // Skipped rather than zero-filled: a band that has moved off the region entirely
                // changes the fingerprint's length, which Differs already treats as a change.
                if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                cells.AddRange(Summarise(data, clipped, TextBandCellsY));
            }

            return new FrameFingerprint([.. cells]);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Whether <paramref name="other"/> shows something meaningfully different. A null or
    /// differently shaped counterpart counts as changed — there is nothing to compare against, and
    /// treating that as "unchanged" would strand the region.
    /// </summary>
    public bool Differs(FrameFingerprint? other)
    {
        if (other is null || other._cells.Length != _cells.Length) return true;
        if (_cells.Length == 0) return false;

        int changed = 0;
        for (int i = 0; i < _cells.Length; i++)
            if (Math.Abs(_cells[i] - other._cells[i]) > CellTolerance)
                changed++;

        return changed * 100 > _cells.Length * ChangedCellPercent;
    }

    private static byte[] Summarise(BitmapData data, Rectangle area, int cellsY)
    {
        var totals = new long[CellsX * cellsY];
        var counts = new int[CellsX * cellsY];

        for (int y = area.Top; y < area.Bottom; y += SampleStep)
        {
            int cellY = (y - area.Top) * cellsY / area.Height;
            nint row = data.Scan0 + y * data.Stride;

            for (int x = area.Left; x < area.Right; x += SampleStep)
            {
                int cellX = (x - area.Left) * CellsX / area.Width;
                int value = Marshal.ReadInt32(row, x * 4);

                // Perceived brightness rather than the raw channels: it is one number instead of
                // three, and it is the axis text actually separates itself from its background on.
                int b = value & 0xFF;
                int g = (value >> 8) & 0xFF;
                int r = (value >> 16) & 0xFF;
                int luminance = (r * 299 + g * 587 + b * 114) / 1000;

                int cell = cellY * CellsX + cellX;
                totals[cell] += luminance;
                counts[cell]++;
            }
        }

        var cells = new byte[totals.Length];
        for (int i = 0; i < totals.Length; i++)
            cells[i] = counts[i] == 0 ? (byte)0 : (byte)(totals[i] / counts[i]);

        return cells;
    }
}
