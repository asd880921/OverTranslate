using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Forms;
using OverTranslate.Services.Realtime.Capture;

namespace WgcProbe;

/// <summary>
/// The go/no-go for capturing a whole monitor with OverTranslate's own overlays excluded: does the
/// excluded region come back showing what is <i>underneath</i> the overlay, or does it come back
/// black?
/// </summary>
/// <remarks>
/// Everything about that path turned on this one answer and nothing in the documentation gave it. A
/// subtitle layer sits directly over the text it translates, so an exclusion that punches a hole —
/// leaving black, or the desktop background, or the last thing composed there — removes exactly the
/// pixels recognition exists to read, and the whole approach is worth nothing. An exclusion that
/// composes the scene without that window is worth everything: it is structural isolation for the
/// full-screen path, the same kind the window path already has.
///
/// Measured through <see cref="WgcMonitorCaptureBackend"/> itself rather than a chain assembled
/// here, so what is being answered for is the code a session runs on — the exclusion list, the
/// refusal to read a frame composed before it took effect, and the crop from the monitor's origin,
/// which is the part that goes wrong quietly on a display to the left of the primary.
///
/// Built like <see cref="OverlayTest"/> and for the same reason: both windows are the probe's own,
/// so the measurement cannot be invalidated by whatever the user does to their desktop mid-run. The
/// source window is filled white with black text, the stand-in subtitle layer is filled a magenta
/// nothing else on a screen produces, and the region under the layer is then counted three ways —
/// magenta means the exclusion did not happen, black means it punched a hole, white means the scene
/// was composed without the overlay. The desktop grab of the same rectangle is the control: it must
/// see the layer, or nothing was proved.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class ExclusionTest
{
    private static System.Drawing.Color Marker => ProbeWindow.Marker;

    public static int Run(Rectangle sourceBounds, string outputDirectory)
    {
        if (!WgcCapability.SupportsWindowExclusion)
        {
            Console.Error.WriteLine("this system has no window exclusion list — nothing to measure");
            return 2;
        }

        // Well inside the source window, so a few pixels of frame either way cannot decide anything.
        var overlayBounds = Rectangle.Inflate(sourceBounds, -sourceBounds.Width / 4, -sourceBounds.Height / 4);

        using var source = ProbeWindow.Show(sourceBounds, opaqueWhite: true, out var sourceHwnd);
        Console.WriteLine($"source window          hwnd={sourceHwnd:X} {sourceBounds.Width}x{sourceBounds.Height} at {sourceBounds.X},{sourceBounds.Y}");

        using var overlay = ProbeWindow.Show(overlayBounds, opaqueWhite: false, out var overlayHwnd);
        Console.WriteLine($"overlay                hwnd={overlayHwnd:X} {overlayBounds.Width}x{overlayBounds.Height} at {overlayBounds.X},{overlayBounds.Y}");

        var monitor = WgcMonitorCaptureBackend.MonitorFor(overlayBounds);
        if (monitor == IntPtr.Zero) return Fail("no monitor under the overlay");
        Console.WriteLine($"monitor                hmonitor={monitor:X}");

        // The control, with the overlay up: what anything reading the screen sees today.
        using var control = new DesktopGrabCaptureBackend(overlaysHiddenFromCapture: true);
        using var onScreen = control.GrabRegion(overlayBounds);
        if (onScreen is null) return Fail("the desktop grab produced nothing");

        // What the session hands the backend: a snapshot that changes as overlays come and go. Here
        // it starts with the one overlay and gains a second one further down.
        var overlays = new List<IntPtr> { overlayHwnd };

        // What the screen looks like around its own edge before anything is captured, for the
        // indicator measurement below.
        using var ringBefore = GrabScreenRing(overlayBounds);

        using var backend = WgcMonitorCaptureBackend.TryCreate(
            () => monitor, () => Volatile.Read(ref overlays).ToArray());
        if (backend is null) return Fail("could not start monitor capture with the overlay excluded");

        // Windows draws a coloured frame around anything being captured. Around one window that is a
        // cost; around the whole screen it is a border the user stares at for the length of a
        // session, so it is measured here rather than discovered by them. Reported, not judged — the
        // numbers only say something changed, and the written PNGs are what to look at.
        using var ringAfter = GrabScreenRing(overlayBounds);
        Console.WriteLine($"screen edge changed    {RingChange(ringBefore, ringAfter):P1}");
        Directory.CreateDirectory(outputDirectory);
        var ringStamp = DateTime.Now.ToString("HHmmss");
        ringBefore.Save(Path.Combine(outputDirectory, $"exclusion-edge-before-{ringStamp}.png"), ImageFormat.Png);
        ringAfter.Save(Path.Combine(outputDirectory, $"exclusion-edge-after-{ringStamp}.png"), ImageFormat.Png);

        // The backend has already waited for a frame composed with the exclusion in force, so this
        // is a poll rather than a wait — but the region is asked for a few times, because a poll can
        // legitimately come back empty while the chain is settling.
        Bitmap? captured = null;
        for (var attempt = 0; attempt < 30 && captured is null; attempt++)
        {
            captured = backend.GrabRegion(overlayBounds);
            if (captured is null) Thread.Sleep(100);
        }

        if (captured is null) return Fail("the monitor capture produced no frame for the region");

        var whole = new Rectangle(0, 0, captured.Width, captured.Height);
        var onScreenShare = Share(onScreen, new Rectangle(0, 0, onScreen.Width, onScreen.Height), Marker);
        var capturedShare = Share(captured, whole, Marker);
        var blackShare = Share(captured, whole, System.Drawing.Color.Black);
        var whiteShare = Share(captured, whole, System.Drawing.Color.White);

        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTime.Now.ToString("HHmmss");
        onScreen.Save(Path.Combine(outputDirectory, $"exclusion-onscreen-{stamp}.png"), ImageFormat.Png);
        captured.Save(Path.Combine(outputDirectory, $"exclusion-captured-{stamp}.png"), ImageFormat.Png);
        captured.Dispose();

        Console.WriteLine($"overlay on screen      {onScreenShare:P1}");
        Console.WriteLine($"overlay in capture     {capturedShare:P1}");
        Console.WriteLine($"black in capture       {blackShare:P1}");
        Console.WriteLine($"source content         {whiteShare:P1}");
        Console.WriteLine($"backend                {backend.DescribeActivity()}");
        Console.WriteLine($"written                {outputDirectory}");

        if (onScreenShare < 0.5)
            return Fail("the overlay is not on the screen — the test proved nothing");

        if (capturedShare > 0.001)
        {
            Console.Error.WriteLine("NO-GO: the overlay is still in the frame after the exclusion");
            return 1;
        }

        if (whiteShare < 0.5)
        {
            Console.Error.WriteLine(
                $"NO-GO: the excluded region does not show what is under it (black {blackShare:P1})");
            return 1;
        }

        // A session creates and destroys overlays while it runs — a block added, the bar rebuilt —
        // so an exclusion list set once at the start is a list of the windows that happened to exist
        // then. This is the other half of the answer: a layer that appears afterwards has to be out
        // of the frames too, and the frames composed before it was excluded must not be served.
        var lateBounds = new Rectangle(
            sourceBounds.X + 40, sourceBounds.Y + 40, sourceBounds.Width / 3, sourceBounds.Height / 4);
        using var late = ProbeWindow.Show(lateBounds, opaqueWhite: false, out var lateHwnd);
        Console.WriteLine($"late overlay           hwnd={lateHwnd:X} {lateBounds.Width}x{lateBounds.Height} at {lateBounds.X},{lateBounds.Y}");

        Volatile.Write(ref overlays, [overlayHwnd, lateHwnd]);

        var started = DateTime.UtcNow;
        double lateShare = 1;
        var polls = 0;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
        {
            polls++;
            using var poll = backend.GrabRegion(lateBounds);
            if (poll is null)
            {
                // What the backend does while the frames catch up with the new list: nothing at all,
                // which is the point.
                Thread.Sleep(100);
                continue;
            }

            lateShare = Share(poll, new Rectangle(0, 0, poll.Width, poll.Height), Marker);
            if (lateShare <= 0.001) break;
            Thread.Sleep(100);
        }

        Console.WriteLine($"late overlay excluded  {lateShare:P1} after {(DateTime.UtcNow - started).TotalMilliseconds:F0}ms over {polls} poll(s)");
        Console.WriteLine($"backend                {backend.DescribeActivity()}");

        if (lateShare > 0.001)
        {
            Console.Error.WriteLine("FAIL: an overlay created after the session started stayed in the frames");
            return 1;
        }

        Console.WriteLine("GO: the excluded region shows the source window underneath the overlay");
        return 0;
    }

    /// <summary>
    /// A band just inside the monitor's own edge, off the screen. Inset because the capture
    /// indicator is drawn on the edge itself rather than outside it — a comparison of the pixels
    /// beyond the screen measures nothing at all.
    /// </summary>
    private static Bitmap GrabScreenRing(Rectangle onThatMonitor)
    {
        var bounds = Screen.FromRectangle(onThatMonitor).Bounds;
        var band = new Rectangle(bounds.X, bounds.Y, bounds.Width, 12);

        var bitmap = new Bitmap(band.Width, band.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(band.Left, band.Top, 0, 0, band.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>How many of two bands' pixels differ.</summary>
    private static double RingChange(Bitmap before, Bitmap after)
    {
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);

        var changed = 0;
        var total = 0;
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                total++;
                if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb()) changed++;
            }
        }

        return total == 0 ? 0 : (double)changed / total;
    }

    /// <summary>How much of one rectangle of a frame is exactly this colour.</summary>
    private static double Share(Bitmap frame, Rectangle region, System.Drawing.Color colour)
    {
        var area = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
        if (area.Width <= 0 || area.Height <= 0) return 0;

        var hits = 0;
        var total = 0;
        for (var y = area.Top; y < area.Bottom; y += 4)
        {
            for (var x = area.Left; x < area.Right; x += 4)
            {
                total++;
                var pixel = frame.GetPixel(x, y);
                if (pixel.R == colour.R && pixel.G == colour.G && pixel.B == colour.B) hits++;
            }
        }

        return total == 0 ? 0 : (double)hits / total;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
