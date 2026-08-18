using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Where the showcase capture takes its picture from, and where the subtitle layers land on it.
/// </summary>
/// <remarks>
/// <para>The picture used to be a <c>CopyFromScreen</c> of the session's screen, and the overlays
/// were placed relative to that same screen — one rectangle, so the arithmetic could not disagree
/// with itself. It now comes from the capture backend and is narrowed to what that backend can
/// actually account for (#105), which in 視窗擷取 is the window rather than the screen. That
/// introduces a second rectangle and therefore an offset that can be wrong, and wrong here does not
/// throw: it produces a picture with the subtitles slid off their source by the distance between the
/// window and the screen corner.</para>
///
/// <para>So these check the two things that changed: the size of the picture, and where an overlay
/// lands on it. A stub backend stands in for the real one because neither WGC nor a real monitor is
/// available in a test run, and neither is what is in question — the arithmetic is.</para>
/// </remarks>
public class RealtimeShowcaseFramingTests
{
    private static readonly System.Drawing.Rectangle Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void WholeScreenSourceComposesTheWholeScreen()
    {
        using var backend = new StubBackend(Screen);

        var composed = OnStaThread(() => RealtimeShowcaseCapture.Compose(backend, Screen, []));

        Assert.NotNull(composed);
        Assert.Equal(Screen.Width, composed.PixelWidth);
        Assert.Equal(Screen.Height, composed.PixelHeight);
        Assert.Equal(Screen, backend.Requested);
    }

    [Fact]
    public void AWindowSourceNarrowsThePictureToTheWindow()
    {
        // What 視窗擷取 looks like: the backend holds one window somewhere on the screen, and asking
        // it for the whole screen would return that window on a field of black.
        var window = new System.Drawing.Rectangle(300, 200, 800, 600);
        using var backend = new StubBackend(window);

        var composed = OnStaThread(() => RealtimeShowcaseCapture.Compose(backend, Screen, []));

        Assert.NotNull(composed);
        Assert.Equal(window.Width, composed.PixelWidth);
        Assert.Equal(window.Height, composed.PixelHeight);
        Assert.Equal(window, backend.Requested);
    }

    [Fact]
    public void AnOverlayLandsAtItsScreenPositionRelativeToTheFrame()
    {
        var window = new System.Drawing.Rectangle(300, 200, 800, 600);
        var overlay = new System.Drawing.Rectangle(500, 350, 40, 30);
        using var backend = new StubBackend(window);

        var composed = OnStaThread(() => RealtimeShowcaseCapture.Compose(
            backend, Screen, [new RealtimeShowcaseCapture.Overlay(overlay, SolidRed(overlay.Width, overlay.Height))]));

        Assert.NotNull(composed);

        // The overlay sits 200 across and 150 down from the window's own corner, not from the
        // screen's — offsetting by the screen would put it 300,200 further along and off the block
        // it is supposed to cover.
        Assert.Equal(Colors.Red, PixelAt(composed, 200 + 20, 150 + 15));

        // And nothing was drawn where the overlay is not.
        Assert.Equal(Colors.Black, PixelAt(composed, 10, 10));
        Assert.Equal(Colors.Black, PixelAt(composed, 200 + 60, 150 + 15));
    }

    [Fact]
    public void ABackendWithNoFrameProducesNothing()
    {
        // Before the first frame, or after the source is lost. The caller shows "capture failed"
        // rather than saving a picture of whatever happened to be composable.
        using var backend = new StubBackend(System.Drawing.Rectangle.Empty);

        var composed = OnStaThread(() => RealtimeShowcaseCapture.Compose(backend, Screen, []));

        Assert.Null(composed);
    }

    /// <summary>A backend that hands out flat black for whatever it is asked, and remembers what
    /// it was asked for.</summary>
    private sealed class StubBackend(System.Drawing.Rectangle sourceBounds) : IRealtimeCaptureBackend
    {
        public string Name => "Stub";

        public bool IsIsolated => true;

        public System.Drawing.Rectangle SourceBounds { get; } = sourceBounds;

        public System.Drawing.Rectangle Requested { get; private set; }

        public System.Drawing.Bitmap? GrabRegion(System.Drawing.Rectangle screenBounds)
        {
            Requested = screenBounds;
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return null;

            var bitmap = new System.Drawing.Bitmap(
                screenBounds.Width, screenBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.Black);
            return bitmap;
        }

        public string DescribeActivity() => "stub";

        public void Dispose()
        {
        }
    }

    private static BitmapSource SolidRed(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;         // B
            pixels[i + 1] = 0;     // G
            pixels[i + 2] = 255;   // R
            pixels[i + 3] = 255;   // A
        }

        var image = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Composing uses WPF drawing objects, which need an STA thread.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
        return result;
    }
}
