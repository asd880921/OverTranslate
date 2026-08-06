using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Services;

// Physical-pixel geometry for the windows that must line up with a screenshot.
//
// SystemParameters.VirtualScreen* cannot serve this purpose: they are DIP scaled by the system DPI,
// while a screenshot is captured in pixels. The two agree only when every monitor runs at the same
// scale, so relying on them makes a mixed-DPI desktop place the overlay against a rectangle that
// does not exist.
internal static class ScreenGeometry
{
    private const int WM_DPICHANGED = 0x02E0;

    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static Rectangle VirtualDesktopBounds()
    {
        var screens = Screen.AllScreens;
        int left   = screens.Min(s => s.Bounds.Left);
        int top    = screens.Min(s => s.Bounds.Top);
        int right  = screens.Max(s => s.Bounds.Right);
        int bottom = screens.Max(s => s.Bounds.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    // Sizes a window in pixels and keeps it there. Window.Left/Width are DIP converted with the DPI
    // of whichever monitor WPF assigned the window; for a desktop-spanning window that is an
    // arbitrary pick between monitors, so on a mixed-DPI desktop those properties cannot express
    // the wanted rectangle at all.
    //
    // Call after the handle exists (OnSourceInitialized) and before reading the window's DPI: this
    // is what settles which monitor the window belongs to.
    public static void PinPhysicalBounds(Window window, Rectangle bounds)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;

        // Refusing WM_DPICHANGED also freezes WPF's render scale for this window, which is what a
        // window covering several monitors needs: one scale for the whole capture. Accepting the
        // suggested rectangle would instead resize the window off the pixels it was drawn for.
        source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg != WM_DPICHANGED) return IntPtr.Zero;
            handled = true;
            Apply(source.Handle, bounds);
            return IntPtr.Zero;
        });

        Apply(source.Handle, bounds);
    }

    private static void Apply(IntPtr hwnd, Rectangle bounds)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
