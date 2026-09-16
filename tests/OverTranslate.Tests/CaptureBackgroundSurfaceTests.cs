using System.Drawing;
using OverTranslate.Services;
using Xunit;
using Rect = System.Windows.Rect;
using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Tests;

public class CaptureBackgroundSurfaceTests
{
    [Fact]
    public void Create_RemovesSourceBandsBeforeBlurringAndPreservesColourVariation()
    {
        using var clean = Gradient();
        using var dirty = (Bitmap)clean.Clone();
        Rect[] lines = [new(25, 30, 205, 15), new(25, 75, 205, 15), new(25, 120, 205, 15)];
        using (var g = Graphics.FromImage(dirty))
            foreach (var line in lines) g.FillRectangle(Brushes.Black, (float)line.X, (float)line.Y, (float)line.Width, (float)line.Height);
        var block = new TranslatedBlock("source", "譯文", new(20, 20, 220, 125), lines);
        var a = CaptureBackgroundSurface.Create(clean, block, lines, System.Windows.Media.Colors.Black);
        var b = CaptureBackgroundSurface.Create(dirty, block, lines, System.Windows.Media.Colors.Black);
        Assert.NotNull(a); Assert.NotNull(b);
        var pixels = Pixels(b!);
        Assert.Equal(Pixels(a!), pixels);
        int width = b!.Image.PixelWidth, height = b.Image.PixelHeight;
        Assert.True(pixels[(width - 1) * 4 + 2] - pixels[2] > 25, "Horizontal red variation was lost.");
        Assert.True(pixels[(height - 1) * width * 4 + 1] - pixels[1] > 25, "Vertical green variation was lost.");
        Assert.True(b.Image.IsFrozen);
        Assert.True(b.CreateBrush().IsFrozen);
        AssertOpaqueAndReadable(b);
    }

    [Fact]
    public void Create_KeepsWholePlateReadableAcrossBrightAndDarkAreas()
    {
        using var frame = new Bitmap(200, 100);
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            int c = x * 255 / (frame.Width - 1);
            frame.SetPixel(x, y, Color.FromArgb(c, c, c));
        }
        var block = new TranslatedBlock("a", "字", new(10, 10, 180, 80));
        var surface = CaptureBackgroundSurface.Create(frame, block, [new(20, 40, 160, 20)], System.Windows.Media.Colors.Red);
        Assert.NotNull(surface);
        Assert.Equal(System.Windows.Media.Colors.Red, surface!.TextColor);
        AssertOpaqueAndReadable(surface!);
    }

    [Fact]
    public void Create_PinkLabelKeepsWhiteTextAndDoesNotImportWhitePageIntoButton()
    {
        using var frame = new Bitmap(160, 80);
        var pink = Color.FromArgb(245, 0, 95);
        using (var g = Graphics.FromImage(frame))
        using (var brush = new SolidBrush(pink))
        {
            g.Clear(Color.White);
            g.FillRectangle(brush, 30, 20, 100, 36);
            for (int x = 42; x < 120; x += 12) g.FillRectangle(Brushes.White, x, 28, 3, 20);
        }
        var background = MediaColor.FromRgb(pink.R, pink.G, pink.B);
        var block = new TranslatedBlock("release", "發行", new(38, 25, 84, 26), BackgroundColor: background);
        var surface = CaptureBackgroundSurface.Create(frame, block, [block.Bounds], System.Windows.Media.Colors.White);
        Assert.NotNull(surface);
        Assert.Equal(System.Windows.Media.Colors.White, surface!.TextColor);
        Assert.Equal(1, surface.Image.PixelWidth);
        Assert.Equal(new byte[] { pink.B, pink.G, pink.R, 255 }, Pixels(surface));
        AssertOpaqueAndReadable(surface);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_PreservesWhiteOrBlackTextByChangingPlateInstead(bool white)
    {
        using var frame = Gradient();
        var foreground = white ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black;
        var block = new TranslatedBlock("source", "譯文", new(20, 20, 220, 125));
        var surface = CaptureBackgroundSurface.Create(frame, block, [new(30, 60, 180, 20)], foreground);
        Assert.NotNull(surface);
        Assert.Equal(foreground, surface!.TextColor);
        AssertOpaqueAndReadable(surface);
    }

    [Fact]
    public void Create_FallsBackWhenNoNonTextObservationsExist()
    {
        using var frame = Gradient();
        var block = new TranslatedBlock("a", "字", new(0, 0, frame.Width, frame.Height));
        Assert.Null(CaptureBackgroundSurface.Create(frame, block, [block.Bounds], System.Windows.Media.Colors.Black));
    }

    [Fact]
    public void Create_ClipsOffscreenBandsAndKeepsOutputOpaque()
    {
        using var frame = Gradient();
        var block = new TranslatedBlock("a", "字", new(-20, -10, 180, 130));
        var surface = CaptureBackgroundSurface.Create(frame, block,
            [new(-20, 20, 180, 20), new(-20, 70, 180, 20)], System.Windows.Media.Colors.Black);
        Assert.NotNull(surface);
        AssertOpaqueAndReadable(surface!);
    }

    private static Bitmap Gradient()
    {
        var frame = new Bitmap(260, 170);
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
            frame.SetPixel(x, y, Color.FromArgb(160 + x * 80 / 260, 140 + y * 90 / 170, 230 - x * 60 / 260));
        return frame;
    }

    private static byte[] Pixels(CaptureBackgroundSurface surface)
    {
        var bytes = new byte[surface.Image.PixelWidth * surface.Image.PixelHeight * 4];
        surface.Image.CopyPixels(bytes, surface.Image.PixelWidth * 4, 0);
        return bytes;
    }

    private static void AssertOpaqueAndReadable(CaptureBackgroundSurface surface)
    {
        var pixels = Pixels(surface);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 3]);
            var color = MediaColor.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            Assert.True(OverlayTextColor.ContrastRatio(surface.TextColor, color) >= 3.5);
        }
    }
}
