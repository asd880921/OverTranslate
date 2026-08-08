using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NLog;
using Rect = System.Windows.Rect;
// Both imaging worlds carry a PixelFormat; this file grabs with GDI+ and composes with WPF.
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Produces the picture a realtime session looks like, for showing the feature to someone who is not
/// sitting at the machine.
/// </summary>
/// <remarks>
/// The subtitle layers carry WDA_EXCLUDEFROMCAPTURE (see <see cref="WindowCaptureShield"/>), so no
/// screenshot tool, screen recorder or meeting share can see them — and that cannot be relaxed while
/// a session runs: the scrim covers the source line, so a loop that could capture its own overlay
/// would be reading a band of its own translation and would never see the original change again.
///
/// Composing the picture here sidesteps that entirely, and the exclusion is what makes it exact
/// rather than merely close: the grab underneath is guaranteed to contain no overlay of ours, so
/// there is nothing to subtract, no double-drawn scrim, and no waiting for the compositor to present
/// a frame without them. Nothing has to be paused and no window style is touched.
/// </remarks>
internal static class RealtimeShowcaseCapture
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>One block's rendered subtitles and where on the screen they belong, in pixels.</summary>
    public readonly record struct Overlay(Rectangle Bounds, BitmapSource Image);

    /// <summary>
    /// Grabs <paramref name="screenBounds"/> and draws each overlay onto it at its own position.
    /// Null when the screen cannot be grabbed; an empty <paramref name="overlays"/> is the caller's
    /// business, not an error here.
    /// </summary>
    /// <remarks>Must run on the UI thread — the compositing uses WPF drawing objects.</remarks>
    public static BitmapSource? Compose(Rectangle screenBounds, IReadOnlyList<Overlay> overlays)
    {
        var background = GrabScreen(screenBounds);
        if (background is null) return null;

        var width = background.PixelWidth;
        var height = background.PixelHeight;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(background, new Rect(0, 0, width, height));

            foreach (var overlay in overlays)
            {
                // The block's bounds are absolute screen pixels; the grab starts at the screen's
                // own origin, which on a secondary monitor is not 0,0.
                context.DrawImage(overlay.Image, new Rect(
                    overlay.Bounds.Left - screenBounds.Left,
                    overlay.Bounds.Top - screenBounds.Top,
                    overlay.Bounds.Width,
                    overlay.Bounds.Height));
            }
        }

        var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    private static BitmapSource? GrabScreen(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        try
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, GdiPixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            return BitmapInterop.ToBitmapSource(bitmap);
        }
        catch (Exception ex)
        {
            // Same transient causes as the session's own grab — a secure desktop, a display being
            // reconfigured. Worth a line either way: unlike the polling loop, which simply skips the
            // poll, this one was asked for by a user who is waiting for a picture.
            Log.Warn(ex, "Could not grab {Bounds} for a realtime showcase capture", bounds);
            return null;
        }
    }
}
