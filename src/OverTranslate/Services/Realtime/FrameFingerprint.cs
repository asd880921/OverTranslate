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
    /// <remarks>
    /// Raised from 12 after measuring what each kind of change actually produces. At 12 a uniform
    /// 16-level brightening of the band — a scene getting lighter behind an unchanged subtitle —
    /// moved 84.8% of the cells, a bigger signal than replacing the subtitle itself, and the loop
    /// duly recognised the same words again. It showed: over a whole live session 48% of the reads
    /// that followed a gap under half a second came back identical to what was already on screen.
    ///
    /// Measured shares of cells moved, over a 1226x196 band:
    ///
    /// <code>
    ///                              tolerance 12   16     24
    ///   background +16 levels           84.8%    0.0%   0.0%
    ///   subtitle replaced, same length  11.3%   10.2%   6.3%
    ///   subtitle replaced, long line    25.0%   21.5%  18.4%
    ///   subtitle disappears             14.5%   14.5%  13.7%
    /// </code>
    ///
    /// 16 removes the drift entirely while every real change still clears
    /// <see cref="ChangedCellPercent"/> two to four times over. Going further closes that margin —
    /// at 24 a same-length replacement is down to 6.3% against a 5% bar — for nothing that 16 has
    /// not already dealt with.
    /// </remarks>
    private const int CellTolerance = 16;

    /// <summary>
    /// What share of cells must have changed before the frame has. A single cell over the tolerance
    /// is a glint or a cursor; a line of text changing takes a good fraction of the grid with it.
    /// </summary>
    internal const int ChangedCellPercent = 5;

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

    /// <summary>
    /// Whether a picture drawn from <paramref name="other"/> would still look right, as opposed to
    /// whether the words in it have changed.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="Differs"/>, and deliberately a stricter one.
    /// <see cref="Differs"/> asks whether recognition would read something new, and
    /// <see cref="CellTolerance"/> is tuned to say no when a scene merely brightens behind an
    /// unchanged subtitle — the measured table above is that decision. For a repaired background the
    /// same drift is exactly what matters: the patch was interpolated from pixels that have since
    /// moved, so keeping it leaves a rectangle of the old shade sitting in the new scene.
    ///
    /// Hence its own pair of thresholds. They are reasoned rather than measured, unlike the ones
    /// above: 4 levels is above the dithering and compression noise a still picture produces and far
    /// below anything a reader can see, and one cell in a hundred is enough to catch a change that
    /// touches only part of the band.
    /// </remarks>
    public bool StillLooksLike(FrameFingerprint? other) =>
        other is not null &&
        _cells.Length == other._cells.Length &&
        ChangedShare(other, RepaintTolerance) <= RepaintChangedShare;

    /// <inheritdoc cref="StillLooksLike"/>
    private const int RepaintTolerance = 4;

    /// <inheritdoc cref="StillLooksLike"/>
    private const double RepaintChangedShare = 0.01;

    /// <summary>
    /// The share of cells that moved by more than <paramref name="tolerance"/>, which is the number
    /// <see cref="Differs"/> compares against <see cref="ChangedCellPercent"/>. Exposed so the two
    /// thresholds can be chosen against measured margins rather than argued about.
    /// </summary>
    internal double ChangedShare(FrameFingerprint? other, int tolerance)
    {
        if (other is null || other._cells.Length != _cells.Length || _cells.Length == 0) return 1;

        var changed = 0;
        for (var i = 0; i < _cells.Length; i++)
            if (Math.Abs(_cells[i] - other._cells[i]) > tolerance)
                changed++;

        return (double)changed / _cells.Length;
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
