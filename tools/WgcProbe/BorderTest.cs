using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using OverTranslate.Services.Realtime.Capture;

namespace WgcProbe;

/// <summary>
/// Whether capturing a window draws the system's capture indicator around it on the user's screen,
/// and whether this application is allowed to turn that off.
/// </summary>
/// <remarks>
/// The one product question window capture cannot be adopted without answering. A realtime session
/// runs for hours over a game; a coloured frame around that game for the whole sitting is a visible
/// cost the desktop-grab path never had, and it is not something a user can be asked to tolerate
/// silently.
///
/// Turning it off is documented as needing consent — <c>GraphicsCaptureAccess.RequestAccessAsync</c>
/// with the borderless kind — granted against a package capability that this application, shipped
/// unpackaged through Velopack, has no identity to declare. So the question is asked in two parts:
/// does the border appear, and does asking to remove it work here. Measured rather than reasoned
/// about, because the answer decides whether a whole backend is usable.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class BorderTest
{
    public static int Run(Rectangle sourceBounds, string outputDirectory)
    {
        using var source = ProbeWindow.Show(sourceBounds, opaqueWhite: true, out var hwnd);
        Console.WriteLine($"source window          hwnd={hwnd:X} {sourceBounds.Width}x{sourceBounds.Height} at {sourceBounds.X},{sourceBounds.Y}");

        // A ring just outside the window is where the indicator is drawn, and looking only there
        // keeps the window's own content — which may animate — out of the comparison.
        var ring = Rectangle.Inflate(sourceBounds, 8, 8);

        using var before = Grab(ring);
        using var backend = WgcWindowCaptureBackend.TryCreate(() => hwnd);
        if (backend is null)
        {
            Console.Error.WriteLine("could not start window capture");
            return 2;
        }

        // Long enough for the indicator to appear and finish whatever it animates on the way in.
        Thread.Sleep(1500);
        using var after = Grab(ring);

        var changed = RingDifference(before, after, sourceBounds.Width, sourceBounds.Height);

        // Asked after the indicator has been measured, so the two answers cannot be confused.
        var borderless = backend.TryHideCaptureBorder();
        Thread.Sleep(1000);
        using var afterRequest = Grab(ring);
        var stillThere = RingDifference(before, afterRequest, sourceBounds.Width, sourceBounds.Height);

        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTime.Now.ToString("HHmmss");
        before.Save(Path.Combine(outputDirectory, $"border-before-{stamp}.png"), ImageFormat.Png);
        after.Save(Path.Combine(outputDirectory, $"border-after-{stamp}.png"), ImageFormat.Png);
        afterRequest.Save(Path.Combine(outputDirectory, $"border-afterrequest-{stamp}.png"), ImageFormat.Png);

        // A threshold rather than a count, and deliberately low: the indicator is a line one or two
        // pixels wide against a ring several pixels deep, so it moves this number by very little
        // even when it is unmistakable on the saved image. Which is why the image is saved — the
        // number says look, the picture says what.
        Console.WriteLine($"ring changed on capture {changed:P1}");
        Console.WriteLine(changed > 0.002
            ? "  → something appeared at the window's edge; confirm on border-after-*.png"
            : "  → nothing appeared at the window's edge");
        Console.WriteLine($"borderless property     {WgcCapability.CanRequestBorderless}");
        Console.WriteLine($"borderless request      {borderless}");
        Console.WriteLine($"ring changed after that {stillThere:P1}");
        Console.WriteLine($"written                 {outputDirectory}");

        return 0;
    }

    private static Bitmap Grab(Rectangle bounds)
    {
        var bitmap = new Bitmap(
            bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// How much of the ring outside the window changed, ignoring the window's interior entirely.
    /// </summary>
    private static double RingDifference(Bitmap before, Bitmap after, int innerWidth, int innerHeight)
    {
        // Four pixels inside the window count as ring. The indicator is drawn on the window's own
        // edge, not outside it, so excluding the interior exactly at the window bounds put the whole
        // thing in the part that was being ignored — measured 0.0% against an image with an
        // unmistakable yellow frame in it.
        var interior = Rectangle.Inflate(
            new Rectangle((before.Width - innerWidth) / 2, (before.Height - innerHeight) / 2, innerWidth, innerHeight),
            -4, -4);

        var changed = 0;
        var total = 0;

        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                if (interior.Contains(x, y)) continue;

                total++;
                if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb()) changed++;
            }
        }

        return total == 0 ? 0 : (double)changed / total;
    }
}
