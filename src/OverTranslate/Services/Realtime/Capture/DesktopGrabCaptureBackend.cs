using System.Drawing;
using System.Drawing.Imaging;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Copies each region straight off the composited desktop with <see cref="Graphics.CopyFromScreen"/>
/// — the original capture path, kept for systems where nothing better is available.
/// </summary>
/// <remarks>
/// The source is what the user is looking at, which includes this application's own subtitle layers.
/// So this backend is isolated only where those layers can be hidden from screen capture, and that
/// is the whole of what <paramref name="overlaysHiddenFromCapture"/> carries: the caller has already
/// asked every layer (see <c>WindowCaptureShield</c>) and hands the answer in, because a backend is
/// not a good place to reach back into windows.
///
/// Two consequences worth knowing before choosing this one. It grabs whatever is in front, so a
/// window covering the watched region is what gets recognised. And it copies a rectangle per region
/// per poll through GDI, which is cheap at these sizes but scales with the region count rather than
/// staying flat the way a shared frame would.
/// </remarks>
public sealed class DesktopGrabCaptureBackend(bool overlaysHiddenFromCapture) : IRealtimeCaptureBackend
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private int _grabs;
    private int _failures;

    // A grab that keeps failing produces no work and no error, which is indistinguishable from a
    // region that simply has nothing in it. Reported once per session so a locked screen does not
    // fill the log at four lines a second.
    private int _failureReported;

    public string Name => "DesktopGrab";

    public bool IsIsolated { get; } = overlaysHiddenFromCapture;

    public Bitmap? GrabRegion(Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return null;

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                screenBounds.Left, screenBounds.Top, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
            Interlocked.Increment(ref _grabs);
            return bitmap;
        }
        catch (Exception ex)
        {
            // A grab can fail transiently — a secure desktop (UAC prompt, lock screen) is the usual
            // cause. Skipping this poll is the whole recovery; the next one is 250ms away.
            Interlocked.Increment(ref _failures);
            if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                Log.Warn(ex, "Realtime screen grab failed for {Bounds}; further failures logged at Debug", screenBounds);
            else
                Log.Debug(ex, "Realtime screen grab failed for {Bounds}", screenBounds);
            bitmap?.Dispose();
            return null;
        }
    }

    public string DescribeActivity() =>
        $"grabs={Volatile.Read(ref _grabs)} failures={Volatile.Read(ref _failures)}";

    public void Dispose()
    {
        // Nothing is held between grabs: each one allocates its bitmap and hands it to the caller.
    }
}
