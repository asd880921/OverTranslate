using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// A cheap fingerprint of a captured region, used to decide whether anything on screen actually
/// changed since the last poll. Recognition is by far the most expensive step in the realtime loop,
/// so the loop is only ever allowed to reach it when this value moves.
/// </summary>
/// <remarks>
/// Sampled rather than exhaustive: a full hash of a 900×200 region touches 180k pixels several
/// times a second for no gain, while every fourth pixel on every fourth row still catches any change
/// large enough to be text. What it deliberately does not do is tolerate change — two frames that
/// differ by a single antialiased edge hash differently, and the loop's stability rule (a change has
/// to survive one further poll before it is acted on) is what absorbs that instead. Doing it here
/// would mean quantising pixels, which is both slower and blind to genuinely small text.
/// </remarks>
internal static class FrameSignature
{
    // Every 4th pixel horizontally and vertically — 1/16th of the region.
    private const int SampleStep = 4;

    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    public static ulong Compute(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            ulong hash = FnvOffsetBasis;

            // Dimensions go into the hash too: a resized region that happens to sample identically
            // is still a different region and must not be mistaken for "nothing happened".
            hash = Mix(hash, (uint)bitmap.Width);
            hash = Mix(hash, (uint)bitmap.Height);

            for (int y = 0; y < data.Height; y += SampleStep)
            {
                nint row = data.Scan0 + y * data.Stride;
                for (int x = 0; x < data.Width; x += SampleStep)
                    hash = Mix(hash, (uint)Marshal.ReadInt32(row, x * 4));
            }

            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static ulong Mix(ulong hash, uint value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }
}
