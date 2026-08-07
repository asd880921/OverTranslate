using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The realtime loop skips recognition whenever the signature is unchanged, so these cover the two
/// ways that can go wrong: a stale frame that looks new (wasted work every poll) and a new frame
/// that looks stale (a subtitle that never updates).
/// </summary>
public class FrameSignatureTests
{
    [Fact]
    public void IdenticalContentProducesTheSameSignature()
    {
        using var first = CreateFrame();
        using var second = CreateFrame();

        Assert.Equal(FrameSignature.Compute(first), FrameSignature.Compute(second));
    }

    [Fact]
    public void ChangedPixelProducesADifferentSignature()
    {
        using var before = CreateFrame();
        using var after = CreateFrame();
        after.SetPixel(8, 8, Color.Black);

        Assert.NotEqual(FrameSignature.Compute(before), FrameSignature.Compute(after));
    }

    [Fact]
    public void SameContentAtADifferentSizeProducesADifferentSignature()
    {
        // A resized block is a different capture area even when everything sampled inside it matches,
        // and it has to be recognised again rather than treated as unchanged.
        using var small = CreateFrame(64, 32);
        using var large = CreateFrame(64, 48);

        Assert.NotEqual(FrameSignature.Compute(small), FrameSignature.Compute(large));
    }

    // Deliberately not a flat fill: a uniform bitmap would hash the same under almost any sampling
    // bug, so the fixture carries a pattern that varies in both directions.
    private static Bitmap CreateFrame(int width = 64, int height = 32)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, x * 3 % 256, y * 7 % 256, (x + y) % 256));

        return bitmap;
    }
}
