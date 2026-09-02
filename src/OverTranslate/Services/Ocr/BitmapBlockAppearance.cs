using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Reads the colours of a line straight off the capture it was recognised from.
/// </summary>
/// <remarks>
/// <para>The two samplers are the ones the overlay already uses to decide what colour to paint a
/// bubble: the background is the most common colour in a ring around the box, and the text colour
/// is the mean of the pixels inside it that are furthest from that background. They are repeated
/// here rather than shared because the overlay's copy ends by tuning the colour for legibility
/// against the bubble it is about to draw, which is a decision about drawing and would be wrong to
/// apply to a measurement.</para>
///
/// <para>A ring rather than the inside of the box, because the inside is mostly glyphs: averaging
/// it returns the text colour blended with the background in whatever proportion the words happen
/// to have, which changes with the sentence rather than with the layout. Most common rather than
/// mean, because a mean of two flat colours is a third colour that is not on the screen anywhere —
/// a box near a card's edge would come back with the average of the card and the page.</para>
///
/// <para>The whole capture is sampled once per pass, one entry per line, and every pair asked about
/// afterwards is answered from that. The alternative — sampling on demand, per pair — reads the
/// same box as many times as it has neighbours.</para>
/// </remarks>
internal sealed class BitmapBlockAppearance : IBlockAppearanceSource
{
    private readonly Dictionary<Rect, BlockAppearance> _sampled = [];
    private readonly BitmapData _data;
    private readonly int _width;
    private readonly int _height;

    private BitmapBlockAppearance(BitmapData data, int width, int height)
    {
        _data = data;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Samples every line of <paramref name="blocks"/> off <paramref name="bitmap"/>, or returns
    /// null when the picture cannot be read.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing, and null rather than an empty source: the grouper treats "no
    /// appearance" as "no visual evidence either way" and decides on geometry alone, which is what
    /// it did before this existed. A capture that cannot be locked is a reason to fall back to that,
    /// not a reason to fail a translation.
    /// </remarks>
    public static IBlockAppearanceSource? Sample(Bitmap bitmap, IReadOnlyList<OcrTextBlock> blocks)
    {
        if (blocks.Count == 0) return null;

        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            var source = new BitmapBlockAppearance(data, bitmap.Width, bitmap.Height);
            foreach (var block in blocks) source.Add(block.Bounds);

            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
        }
    }

    public BlockAppearance For(Rect bounds) =>
        _sampled.TryGetValue(bounds, out var appearance) ? appearance : default;

    private void Add(Rect bounds)
    {
        if (_sampled.ContainsKey(bounds)) return;

        var background = SampleOuterDominantBackground(bounds);
        _sampled[bounds] = new BlockAppearance(background, SampleText(bounds, background));
    }

    private MediaColor SampleOuterDominantBackground(Rect bounds)
    {
        var padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        var padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        var x1 = Math.Clamp((int)bounds.X - padX, 0, _width);
        var y1 = Math.Clamp((int)bounds.Y - padY, 0, _height);
        var x2 = Math.Clamp((int)(bounds.X + bounds.Width) + padX, 0, _width);
        var y2 = Math.Clamp((int)(bounds.Y + bounds.Height) + padY, 0, _height);
        var innerX1 = Math.Clamp((int)bounds.X, 0, _width);
        var innerY1 = Math.Clamp((int)bounds.Y, 0, _height);
        var innerX2 = Math.Clamp((int)(bounds.X + bounds.Width), 0, _width);
        var innerY2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, _height);

        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();

        for (var py = y1; py < y2; py++)
        {
            for (var px = x1; px < x2; px += 2)
            {
                if (px >= innerX1 && px < innerX2 && py >= innerY1 && py < innerY2) continue;

                var (r, g, b) = ReadPixel(px, py);

                // Bucketed on the top four bits of each channel, so the shades anti-aliasing and
                // compression leave behind count as one colour instead of splitting the vote
                // between a hundred near-identical entries.
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                var bucket = buckets.GetValueOrDefault(key);
                buckets[key] = (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1);
            }
        }

        if (buckets.Count == 0) return System.Windows.Media.Colors.White;

        var dominant = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        return MediaColor.FromRgb(
            (byte)(dominant.R / dominant.Count),
            (byte)(dominant.G / dominant.Count),
            (byte)(dominant.B / dominant.Count));
    }

    private MediaColor SampleText(Rect bounds, MediaColor background)
    {
        var x1 = Math.Clamp((int)bounds.X, 0, _width);
        var y1 = Math.Clamp((int)bounds.Y, 0, _height);
        var x2 = Math.Clamp((int)(bounds.X + bounds.Width), 0, _width);
        var y2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, _height);

        // Two passes: the first finds how far from the background this box gets at all, the second
        // averages only the pixels near that far. A fixed cut-off would read light grey text as
        // background and dark text as half background, and the answer has to hold for both.
        var furthest = 0;
        for (var py = y1; py < y2; py++)
            for (var px = x1; px < x2; px += 2)
                furthest = Math.Max(furthest, DistanceFrom(background, px, py));

        var threshold = Math.Max(60, (int)(furthest * 0.6));
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();

        for (var py = y1; py < y2; py++)
            for (var px = x1; px < x2; px += 2)
            {
                if (DistanceFrom(background, px, py) < threshold) continue;

                var (r, g, b) = ReadPixel(px, py);
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                var bucket = buckets.GetValueOrDefault(key);
                buckets[key] = (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1);
            }

        // A box with nothing in it that stands out from its surroundings. Whatever is there is not
        // text this can measure, so the background is the honest answer: it makes the pair look
        // identical in the foreground and leaves the decision to the other evidence.
        if (buckets.Count == 0) return background;

        // The most common ink rather than the mean of it, which is the difference between reading a
        // heading and reading an average of one. Measured on a product diagram: "Doc Understanding
        // Series" sets the first two words in blue and the third in black, and the mean of that
        // came back 3.6 from plain black — indistinguishable from body text, and the pair merged.
        // Taken as the dominant cluster the same line reads as the blue it mostly is. Anti-aliasing
        // makes this necessary as much as mixed colour does: the edge pixels of every glyph are
        // part background, and there are enough of them to drag a mean a long way.
        var dominant = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        return MediaColor.FromRgb(
            (byte)(dominant.R / dominant.Count),
            (byte)(dominant.G / dominant.Count),
            (byte)(dominant.B / dominant.Count));
    }

    private int DistanceFrom(MediaColor color, int px, int py)
    {
        var (r, g, b) = ReadPixel(px, py);
        return Math.Abs(r - color.R) + Math.Abs(g - color.G) + Math.Abs(b - color.B);
    }

    private (byte R, byte G, byte B) ReadPixel(int px, int py)
    {
        var value = Marshal.ReadInt32(_data.Scan0, py * _data.Stride + px * 4);
        return ((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }
}
