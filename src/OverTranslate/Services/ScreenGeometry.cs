using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int MDT_EFFECTIVE_DPI = 0;
    private const int MONITOR_DEFAULTTONEAREST = 2;

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

    // Scale of the monitor under a physical point. Must be read from the monitor, not from the
    // window being placed: a window reports the DPI of wherever it currently sits, which before the
    // move is not the monitor it is about to land on.
    public static double ScaleAt(int physX, int physY)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = physX, Y = physY }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException) { /* shcore missing — pre-8.1 */ }
        return 1.0;
    }

    // Where a window actually is, in pixels. The counterpart to MoveToPhysical and needed for the
    // same reason: Window.Left/Top/ActualHeight are DIP, converted with the DPI of the monitor WPF
    // believes the window is on, so a window that has been placed with MoveToPhysical cannot be read
    // back through them without the conversion drifting. Empty when the handle does not exist yet.
    public static Rectangle PhysicalBounds(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    // Moves a window to an exact pixel position, leaving its size to WPF. Window.Left/Top would be
    // converted with the DPI of the monitor the window is on before the move, so a window crossing
    // to a monitor at another scale lands off by that scale factor.
    public static void MoveToPhysical(Window window, int physX, int physY)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, physX, physY, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Left/Top stay WPF's own properties, and it re-applies them on later layout and DPI
        // events. Left holding their pre-move values, the window is flung back there and has to be
        // dragged into place again, which reads as a flicker. Safe to assign now: the window is on
        // the target monitor, so this conversion is the inverse of the one WPF will perform.
        if (PresentationSource.FromVisual(window)?.CompositionTarget is CompositionTarget target)
        {
            window.Left = physX / target.TransformToDevice.M11;
            window.Top  = physY / target.TransformToDevice.M22;
        }
    }

    private static void Apply(IntPtr hwnd, Rectangle bounds)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
