using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Hides a window from screen capture, so the realtime loop grabs the application underneath rather
/// than the translation it drew there itself.
/// </summary>
/// <remarks>
/// Without this the loop feeds on its own output: it draws a translated line over the source line,
/// the next grab reads that translation back, recognition returns the target language, and the
/// overlay settles into translating its own text. Hiding the overlay around each grab would work but
/// costs a visible flicker several times a second, whereas <c>WDA_EXCLUDEFROMCAPTURE</c> keeps the
/// window fully visible to the user and simply absent from anything that reads the screen.
/// </remarks>
internal static class WindowCaptureShield
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Windows 10 2004 and later. On anything older SetWindowDisplayAffinity rejects it, which is
    // reported once and then tolerated — see Exclude.
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private static bool _unsupportedReported;

    /// <summary>
    /// Call once the window has a handle (OnSourceInitialized or later). Returns whether the window
    /// is now excluded.
    /// </summary>
    public static bool Exclude(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            return true;

        // Not fatal, and not worth blocking the feature over: the loop's own guard is that
        // recognising its own output produces text identical to what it already rendered, so the
        // region settles instead of looping. Logged once — a per-window warning would repeat for
        // every block on every session.
        if (!_unsupportedReported)
        {
            _unsupportedReported = true;
            Log.Warn(
                "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed (error {Error}); " +
                "realtime overlays may appear in screen grabs on this system",
                Marshal.GetLastWin32Error());
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
