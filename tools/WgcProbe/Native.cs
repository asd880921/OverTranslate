using System.Drawing;
using System.Runtime.InteropServices;

namespace WgcProbe;

/// <summary>
/// The two window tricks the stand-in overlay needs, kept here so <see cref="OverlayTest"/> reads as
/// the experiment it is. Both mirror what the application's own overlays do — see
/// <c>ScreenGeometry.PinPhysicalBounds</c> and <c>WindowStyles.ApplyClickThrough</c> — because an
/// overlay built differently would not be testing the same thing.
/// </summary>
internal static class Native
{
    /// <summary>Places a window at exact physical pixels, past WPF's DIP layer.</summary>
    public static void PinPhysicalBounds(IntPtr hwnd, Rectangle bounds) =>
        SetWindowPos(
            hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Clicks pass through to whatever is underneath, and the window never takes focus.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
