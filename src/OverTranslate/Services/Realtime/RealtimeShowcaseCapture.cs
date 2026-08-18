using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NLog;
using OverTranslate.Services.Realtime.Capture;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Produces the picture a realtime session looks like, for showing the feature to someone who is not
/// sitting at the machine.
/// </summary>
/// <remarks>
/// The picture is composed rather than photographed: the background comes from the session's capture
/// backend and the subtitle layers are drawn onto it from their own visual trees. That is not a
/// workaround for the layers being hard to photograph — it is the exact version of the same picture.
/// The backend's frame is guaranteed to contain no overlay of ours, so there is nothing to subtract,
/// no double-drawn scrim, and no waiting for the compositor to present a frame without them; and the
/// layers are drawn with their translucency intact rather than already blended into whatever was
/// behind them. Nothing has to be paused and no window style is touched.
///
/// It used to grab the screen here with <c>CopyFromScreen</c> and rely on the layers carrying
/// <c>WDA_EXCLUDEFROMCAPTURE</c> to stay out of that grab — which held only on Windows 11 24H2 and
/// later, and silently drew every scrim and every line twice on anything older. Asking the backend
/// removes the version from the question: it is the same source recognition reads, isolated the same
/// way, on every system that can run a session at all.
/// </remarks>
internal static class RealtimeShowcaseCapture
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>One block's rendered subtitles and where on the screen they belong, in pixels.</summary>
    public readonly record struct Overlay(Rectangle Bounds, BitmapSource Image);

    /// <summary>
    /// Takes as much of <paramref name="screenBounds"/> as <paramref name="capture"/> can account
    /// for and draws each overlay onto it at its own position. Null when the backend has no frame to
    /// give; an empty <paramref name="overlays"/> is the caller's business, not an error here.
    /// </summary>
    /// <remarks>
    /// Must run on the UI thread — the compositing uses WPF drawing objects.
    ///
    /// The frame is the requested screen narrowed to
    /// <see cref="IRealtimeCaptureBackend.SourceBounds"/>, which matters only in 指定視窗: there the
    /// backend holds one window, and asking it for a whole screen would return that window on a
    /// field of black. Narrowing makes the picture the window with its subtitles on it, which is what
    /// the session is. In 完整螢幕 the two rectangles are the same monitor and nothing is narrowed.
    /// Overlays outside the frame are clipped by the compositing, as they should be — they are not
    /// over the thing being shown.
    /// </remarks>
    public static BitmapSource? Compose(
        IRealtimeCaptureBackend capture, Rectangle screenBounds, IReadOnlyList<Overlay> overlays)
    {
        var frameBounds = Rectangle.Intersect(screenBounds, capture.SourceBounds);
        var background = GrabFrame(capture, frameBounds);
        if (background is null) return null;

        var width = background.PixelWidth;
        var height = background.PixelHeight;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(background, new Rect(0, 0, width, height));

            foreach (var overlay in overlays)
            {
                // The block's bounds are absolute screen pixels; the frame starts at its own origin,
                // which on a secondary monitor — or on a window anywhere — is not 0,0.
                context.DrawImage(overlay.Image, new Rect(
                    overlay.Bounds.Left - frameBounds.Left,
                    overlay.Bounds.Top - frameBounds.Top,
                    overlay.Bounds.Width,
                    overlay.Bounds.Height));
            }
        }

        var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    private static BitmapSource? GrabFrame(IRealtimeCaptureBackend capture, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            // The backend has produced no frame yet, or has lost its source and the rectangles no
            // longer meet. Both read to the user as "the capture failed", which is what the caller
            // says.
            Log.Warn("No captured frame to compose a realtime showcase capture from");
            return null;
        }

        try
        {
            using var bitmap = capture.GrabRegion(bounds);
            if (bitmap is null)
            {
                // Between frames. Ordinary for the polling loop, which skips the poll — but this one
                // was asked for by a user waiting for a picture, so it is worth a line.
                Log.Warn(
                    "The {Backend} capture backend had no frame for {Bounds} when a realtime " +
                    "showcase capture was asked for", capture.Name, bounds);
                return null;
            }

            return BitmapInterop.ToBitmapSource(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not read {Bounds} for a realtime showcase capture", bounds);
            return null;
        }
    }
}
