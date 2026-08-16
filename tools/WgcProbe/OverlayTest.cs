using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using OverTranslate.Services.Realtime.Capture;

namespace WgcProbe;

/// <summary>
/// The acceptance test the whole rework turns on: put a window over a region that looks and behaves
/// exactly like a realtime subtitle layer, then capture that region both ways and count how much of
/// the overlay came back.
/// </summary>
/// <remarks>
/// This is the question #94 was lost on, and it was lost on it because nobody could ask it directly:
/// the answer depends on the Windows build, the window's transparency implementation and the capture
/// source all at once, and the developer's own machine happens to be on the side where the old path
/// works. So it is asked in numbers, on whatever machine is in front of us, with an overlay built to
/// the same recipe as the real one — <c>AllowsTransparency</c>, topmost, click-through, per-pixel
/// alpha — and the desktop grab measured alongside as the control.
///
/// Both windows are the probe's own, and the source window is one of them. That is not a shortcut
/// around testing against real applications — the <c>region</c> command is for that — it is what
/// makes this one repeatable: run against whatever happens to be on the desktop, the test moves when
/// the user moves a window, and the first attempt at it did exactly that.
///
/// A pass is unambiguous: the desktop grab is full of the marker colour and the window capture has
/// none of it.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class OverlayTest
{
    // Nothing on a real screen is this colour, so counting it needs no tolerance for compression or
    // blending — the overlay is drawn fully opaque over the region it covers.
    private static readonly System.Drawing.Color Marker = System.Drawing.Color.FromArgb(255, 255, 0, 255);

    public static int Run(Rectangle sourceBounds, string outputDirectory)
    {
        // Inset well within the source window, so a few pixels of frame either way cannot decide
        // the result.
        var bounds = Rectangle.Inflate(sourceBounds, -sourceBounds.Width / 4, -sourceBounds.Height / 4);

        using var source = ShowWindow(sourceBounds, opaqueWhite: true, out var sourceHwnd);
        Console.WriteLine($"source window          hwnd={sourceHwnd:X} {sourceBounds.Width}x{sourceBounds.Height} at {sourceBounds.X},{sourceBounds.Y}");

        using var overlay = ShowWindow(bounds, opaqueWhite: false, out _);
        Console.WriteLine($"overlay                {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y}");

        using var backend = WgcWindowCaptureBackend.TryCreate(() => sourceHwnd);
        if (backend is null)
        {
            Console.Error.WriteLine("could not start window capture");
            return 2;
        }

        // Both from the same moment, with the overlay up in both cases.
        using var desktop = GrabDesktop(bounds);
        Bitmap? captured = null;
        for (var attempt = 0; attempt < 30 && captured is null; attempt++)
        {
            captured = backend.GrabRegion(bounds);
            if (captured is null) Thread.Sleep(100);
        }

        if (captured is null)
        {
            Console.Error.WriteLine("window capture produced no frame");
            return 2;
        }

        var desktopShare = MarkerShare(desktop);
        var capturedShare = MarkerShare(captured);

        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTime.Now.ToString("HHmmss");
        desktop.Save(Path.Combine(outputDirectory, $"overlay-desktopgrab-{stamp}.png"), ImageFormat.Png);
        captured.Save(Path.Combine(outputDirectory, $"overlay-wgcwindow-{stamp}.png"), ImageFormat.Png);
        captured.Dispose();

        Console.WriteLine($"overlay in desktop grab {desktopShare:P1}");
        Console.WriteLine($"overlay in window capture {capturedShare:P1}");
        Console.WriteLine($"written                {outputDirectory}");

        // The desktop grab has to see it, or the overlay never rendered and the other number means
        // nothing. A hair of the marker in the window capture is still a failure: this is a colour
        // that only the overlay draws.
        if (desktopShare < 0.5)
        {
            Console.Error.WriteLine("the overlay did not reach the desktop grab — the test proved nothing");
            return 2;
        }

        if (capturedShare > 0.001)
        {
            Console.Error.WriteLine("FAIL: the overlay is present in the window capture");
            return 1;
        }

        Console.WriteLine("PASS: the overlay is absent from the window capture");
        return 0;
    }

    /// <summary>
    /// One window on its own STA thread with its own dispatcher, because the probe's main thread is
    /// busy capturing.
    /// </summary>
    /// <param name="opaqueWhite">
    /// The source window, if true — an ordinary opaque window standing in for the application being
    /// watched. Otherwise the subtitle layer: built exactly the way <c>RealtimeBlockWindow</c> is,
    /// because an overlay built any other way would not be testing the same thing.
    /// </param>
    private static IDisposable ShowWindow(Rectangle bounds, bool opaqueWhite, out IntPtr hwnd)
    {
        var ready = new ManualResetEventSlim(false);
        var handle = IntPtr.Zero;
        System.Windows.Threading.Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = !opaqueWhite,
                Background = new SolidColorBrush(opaqueWhite
                    ? Colors.White
                    : System.Windows.Media.Color.FromArgb(255, Marker.R, Marker.G, Marker.B)),
                Topmost = true,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                // Content, so ContentRendered actually fires and so the source window has something
                // in it that a capture can be seen to have caught.
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = opaqueWhite ? "source window" : "subtitle layer",
                    FontSize = 32,
                    Foreground = new SolidColorBrush(Colors.Black),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            window.SourceInitialized += (_, _) =>
            {
                handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Native.PinPhysicalBounds(handle, bounds);
                if (!opaqueWhite) Native.MakeClickThrough(handle);
            };

            window.ContentRendered += (_, _) => ready.Set();
            window.Show();

            System.Windows.Threading.Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            Console.Error.WriteLine("warning: a probe window did not report itself rendered");

        // The compositor needs a moment past ContentRendered before the pixels are on the glass.
        Thread.Sleep(400);

        hwnd = handle;
        return new Closer(() => dispatcher?.InvokeShutdown());
    }

    private static Bitmap GrabDesktop(Rectangle bounds)
    {
        var bitmap = new Bitmap(
            bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static double MarkerShare(Bitmap bitmap)
    {
        var hits = 0;
        var total = 0;

        // Every fourth pixel in each direction: a sixteenth of the work for an answer that is either
        // near zero or near one.
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                total++;
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R == Marker.R && pixel.G == Marker.G && pixel.B == Marker.B) hits++;
            }
        }

        return total == 0 ? 0 : (double)hits / total;
    }

    private sealed class Closer(Action close) : IDisposable
    {
        public void Dispose() => close();
    }
}
