using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NLog;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Writes out the frames a realtime pass could not read anything in, when asked to.
/// </summary>
/// <remarks>
/// "Recognition found nothing" has causes that the log cannot separate however much is written to
/// it: the line may be framed outside the watched block, clipped by its edge, too small or too faint
/// for the detector, or the grab may have caught the player mid-repaint. All of them look identical
/// from the outside — an empty result — and all of them are obvious in one glance at the frame.
///
/// Off unless <c>OVERTRANSLATE_DUMPFRAMES</c> is set, and deliberately so: these are pictures of
/// whatever the user has on screen. Kept to <see cref="MaxFrames"/> per session so leaving it on
/// cannot quietly fill a disk.
/// </remarks>
internal static class RealtimeFrameDump
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int MaxFrames = 60;

    public static readonly bool IsEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OVERTRANSLATE_DUMPFRAMES"));

    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OverTranslate", "logs", "frames");

    private static int _written;

    public static void SaveUnread(Bitmap frame, int regionId)
    {
        if (!IsEnabled || Interlocked.Increment(ref _written) > MaxFrames) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(
                Directory, $"region{regionId}-{DateTime.Now:HHmmss-fff}-unread.png");

            // Cloned because the caller disposes this frame at the end of its poll, and because
            // Save on a bitmap another thread may still be reading from is not safe.
            using var copy = new Bitmap(frame);
            copy.Save(path, ImageFormat.Png);

            Log.Debug("Saved unread realtime frame to {Path}", path);
        }
        catch (Exception ex)
        {
            // Diagnostics must never be the reason a session stops.
            Log.Warn(ex, "Could not save an unread realtime frame");
        }
    }
}
