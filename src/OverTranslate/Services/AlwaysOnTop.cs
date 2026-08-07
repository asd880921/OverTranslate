using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverTranslate.Services;

/// <summary>
/// Puts a window back on top of the topmost band without taking the focus off whatever the user is
/// actually using.
/// </summary>
/// <remarks>
/// Topmost is a band, not a position: every topmost window in the system shares it, and activating
/// one moves it to the front of the band. That is the whole reason the screenshot overlay is never
/// covered — it calls <c>Activate()</c>, because the user is about to draw on it.
///
/// The realtime layers cannot do that. They sit over a game or a video and must not take its input,
/// so they carry WS_EX_NOACTIVATE and are never activated. The cost is that they enter the band
/// where they were put and never move up again: anything topmost that gets activated afterwards —
/// a chat overlay, a capture tool, another utility — goes above them and stays there.
///
/// SetWindowPos re-inserts a window at the front of the band directly, and SWP_NOACTIVATE means it
/// does so without the focus change that would break the application underneath. Called on a timer,
/// it wins that competition against anything that is not re-asserting itself just as often.
///
/// It does not win against a game in exclusive fullscreen, which bypasses the compositor entirely
/// and cannot be drawn over by any window at all. That case needs borderless windowed mode, and no
/// amount of z-order work substitutes for it.
/// </remarks>
internal static class AlwaysOnTop
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void Reassert(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
