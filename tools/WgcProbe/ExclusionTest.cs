using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using OverTranslate.Services.Realtime.Capture;

namespace WgcProbe;

/// <summary>
/// The go/no-go for capturing a whole monitor with OverTranslate's own overlays excluded: does the
/// excluded region come back showing what is <i>underneath</i> the overlay, or does it come back
/// black?
/// </summary>
/// <remarks>
/// Everything about that plan turns on this one answer and nothing in the documentation gives it. A
/// subtitle layer sits directly over the text it translates, so an exclusion that punches a hole —
/// leaving black, or the desktop background, or the last thing composed there — removes exactly the
/// pixels recognition exists to read, and the whole approach is worth nothing. An exclusion that
/// composes the scene without that window is worth everything: it is structural isolation for the
/// full-screen path, the same kind the window path already has.
///
/// Built like <see cref="OverlayTest"/> and for the same reason: both windows are the probe's own,
/// so the measurement cannot be invalidated by whatever the user does to their desktop mid-run. The
/// source window is filled white with black text, the stand-in subtitle layer is filled a magenta
/// nothing else on a screen produces, and the region under the layer is then counted three ways —
/// magenta means the exclusion did not happen, black means it punched a hole, white means the scene
/// was composed without the overlay.
///
/// The frame examined is not simply the next one. The set call returns the configuration iteration
/// its answer holds from, frames carry the iteration they were composed under, and a frame from
/// before that number legitimately still contains the overlay. Reading one of those would have this
/// test report a failure the system did not commit.
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

        var centre = new POINT
        {
            X = overlayBounds.X + (overlayBounds.Width / 2),
            Y = overlayBounds.Y + (overlayBounds.Height / 2)
        };
        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return Fail("no monitor under the overlay");

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return Fail("could not read the monitor's rectangle");

        var monitorOrigin = new Point(info.rcMonitor.Left, info.rcMonitor.Top);
        Console.WriteLine($"monitor                hmonitor={monitor:X} at {monitorOrigin.X},{monitorOrigin.Y} " +
                          $"{info.rcMonitor.Right - info.rcMonitor.Left}x{info.rcMonitor.Bottom - info.rcMonitor.Top}");

        var item = WgcInterop.CreateItemForMonitor(monitor);
        if (item is null) return Fail("the monitor refused capture");

        var device = WgcInterop.CreateDirect3DDevice(out var rawDevice);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);

        try
        {
            // The pointer would otherwise land in the measured region and be counted as neither the
            // overlay nor the source window.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) session.IsCursorCaptureEnabled = false;
            session.StartCapture();
            Console.WriteLine($"itemSize               {item.Size.Width}x{item.Size.Height}");

            // Before: the control. The overlay must be in this one, or the measurement after it
            // proves nothing at all.
            using var before = WaitForFrame(pool, minimumIteration: null);
            if (before is null) return Fail("no frame arrived before the exclusion was set");

            var iteration = WgcWindowExclusion.TrySet(session, [overlayHwnd], out var detail);
            Console.WriteLine($"exclusion              {detail}");
            if (iteration is not { } applied) return Fail("the exclusion list was not applied");

            var readBack = WgcWindowExclusion.GetApplied(session);
            Console.WriteLine($"reported as excluded   {string.Join(", ", readBack.Select(h => h.ToString("X")))}");

            using var after = WaitForFrame(pool, applied);
            if (after is null) return Fail($"no frame reached configuration iteration {applied}");

            var region = new Rectangle(
                overlayBounds.X - monitorOrigin.X, overlayBounds.Y - monitorOrigin.Y,
                overlayBounds.Width, overlayBounds.Height);

            var beforeShare = Share(before, region, Marker);
            var afterShare = Share(after, region, Marker);
            var blackShare = Share(after, region, System.Drawing.Color.Black);
            var whiteShare = Share(after, region, System.Drawing.Color.White);

            Directory.CreateDirectory(outputDirectory);
            var stamp = DateTime.Now.ToString("HHmmss");
            before.Save(Path.Combine(outputDirectory, $"exclusion-before-{stamp}.png"), ImageFormat.Png);
            after.Save(Path.Combine(outputDirectory, $"exclusion-after-{stamp}.png"), ImageFormat.Png);

            Console.WriteLine($"overlay before         {beforeShare:P1}");
            Console.WriteLine($"overlay after          {afterShare:P1}");
            Console.WriteLine($"black after            {blackShare:P1}");
            Console.WriteLine($"source content after   {whiteShare:P1}");
            Console.WriteLine($"written                {outputDirectory}");

            if (beforeShare < 0.5)
                return Fail("the overlay never reached the monitor capture — the test proved nothing");

            if (afterShare > 0.001)
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

            Console.WriteLine("GO: the excluded region shows the source window underneath the overlay");
            return 0;
        }
        finally
        {
            (device as IDisposable)?.Dispose();
            if (rawDevice != IntPtr.Zero) Marshal.Release(rawDevice);
        }
    }

    /// <summary>
    /// The next frame composed at or after <paramref name="minimumIteration"/>, read back onto the
    /// CPU. Null when none arrived in time.
    /// </summary>
    private static Bitmap? WaitForFrame(Direct3D11CaptureFramePool pool, ulong? minimumIteration)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                Thread.Sleep(50);
                continue;
            }

            var iteration = WgcWindowExclusion.TryGetIteration(frame);
            if (minimumIteration is { } wanted && (iteration is null || iteration < wanted))
            {
                Thread.Sleep(50);
                continue;
            }

            return WgcInterop.ReadBack(frame.Surface, frame.ContentSize.Width, frame.ContentSize.Height);
        }

        return null;
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

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
