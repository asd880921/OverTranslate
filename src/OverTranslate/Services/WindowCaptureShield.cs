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

        // This used to be recorded as not worth blocking the feature over, on the reasoning that the
        // loop guards itself: recognising its own output would produce text identical to what it had
        // already rendered, so the region would settle instead of looping. That guard is real — see
        // TextSimilarity.IsSameContent and the Unchanged outcome in RealtimeTranslationSession — but
        // it never engages here, and a user's log has now shown what happens instead.
        //
        // What comes back off the screen is not the translation. It is the translation composited
        // over whatever the scrim failed to cover, so every reading is a fresh mixture of target and
        // leftover source text and none of them matches the last. Every pass counts as new, and each
        // one re-translates the previous translation. The size goes with it: a CJK reading carries no
        // separate glyph height, so the overlay sizes its font from the detection box and draws about
        // 1.15x the height it read (MaxHeightOverSource), which the next pass reads back as the new
        // box. One measured line went 25px to 71px in nine seconds before the region was too blurred
        // to recognise at all.
        //
        // None of that is recoverable after the fact: the scrim physically covers the source, so the
        // pixels underneath are gone from the grab and no amount of filtering brings them back.
        //
        // The cause is this window, not the machine. WPF's AllowsTransparency renders through
        // UpdateLayeredWindow, and Windows does not support display affinity on that kind of layered
        // window — error 8 is ERROR_NOT_ENOUGH_MEMORY and has nothing to do with memory. It was
        // fixed in Windows 11 24H2 (build 26100), so this fails on every earlier Windows and works
        // on every later one, which is why it survived this long unnoticed. Tracked in #94, together
        // with the fact that no caller does anything with the value returned below.
        //
        // Logged once — a per-window warning would repeat for every block on every session.
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
