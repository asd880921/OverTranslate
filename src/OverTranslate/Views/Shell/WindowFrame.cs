using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OverTranslate.Views.Shell;

/// <summary>
/// The parts of the system window frame a shell window still needs once it draws its own title bar:
/// the maximised size, and the outer edge the desktop compositor draws around it.
/// </summary>
/// <remarks>
/// Windows maximises such a window to the monitor's full size plus its own resize border, on the
/// assumption that the border is non-client and therefore off-screen. Under WindowChrome it is not
/// — it is the application's own content — so the window's edges, and part of the caption buttons
/// with them, end up past the screen, and the taskbar is covered.
///
/// WM_GETMINMAXINFO is where that size is decided, so it is the one place to answer it: the work
/// area of the monitor the window is on, in physical pixels, which is exactly what the message asks
/// for. Insetting the content by a guessed number of device-independent units instead would be
/// wrong at every scale factor other than 100%, and wrong again on a second monitor whose taskbar
/// is somewhere else.
/// </remarks>
internal static class WindowFrame
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    private const int WM_SIZE = 0x0005;

    /// <summary>
    /// The radius the compositor rounds a window to on Windows 11, in device-independent units,
    /// for the fallback below to match.
    /// </summary>
    private const double LegacyCornerRadius = 8;

    /// <summary>
    /// Whether the desktop compositor rounds windows itself.
    /// </summary>
    /// <remarks>
    /// DWMWA_WINDOW_CORNER_PREFERENCE arrived with Windows 11 (build 22000) and is rejected outright
    /// below it — DwmSetWindowAttribute returns E_INVALIDARG and the window simply stays square. So
    /// this is the line between the two paths <see cref="ApplyAppearance"/> takes, and it is read
    /// once: the answer cannot change while the process is running.
    ///
    /// OVERTRANSLATE_LEGACY_WINDOW_CORNERS=1 forces the Windows 10 path on where it is not needed,
    /// which is the only way to look at that path on a Windows 11 development machine.
    /// </remarks>
    private static readonly bool DwmRoundsCorners =
        Environment.OSVersion.Version is { Major: >= 10, Build: >= 22000 }
        && Environment.GetEnvironmentVariable("OVERTRANSLATE_LEGACY_WINDOW_CORNERS") != "1";

    /// <summary>
    /// Takes the frame over for as long as <paramref name="window"/> lives. Safe to call before the
    /// window has a handle — everything that needs one waits for it.
    /// </summary>
    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource existing)
        {
            existing.AddHook(HookFor(window));
            RestoreSystemAnimations(existing.Handle);
            ApplyAppearance(window);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(HookFor(window));
                RestoreSystemAnimations(source.Handle);
            }
            ApplyAppearance(window);
        }
    }

    /// <summary>
    /// Rounds the window's corners and paints its outer edge in the application's own border
    /// colour. Call again when the theme changes — the edge is drawn by the compositor, so a
    /// DynamicResource never reaches it.
    /// </summary>
    /// <remarks>
    /// The compositor draws this frame, rather than the window drawing a rounded border of its own
    /// the way 取詞翻譯 does. That popup can afford AllowsTransparency because it is a small,
    /// fixed-size card; here it would make the whole window a layered surface, which costs
    /// ClearType on every glyph in the application and hardware acceleration with it — a bad trade
    /// for a window that is mostly text. Asking DWM for the same shape keeps the text sharp, and
    /// keeps the corners the ones the rest of Windows 11 draws.
    /// </remarks>
    public static void ApplyAppearance(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Windows 10 knows neither of the two attributes below: it neither rounds a window on
        // request nor lets one colour its own outer edge. The shape is worth having anyway, so it
        // is cut out by hand there; the edge colour has nowhere to go and is simply not asked for.
        if (!DwmRoundsCorners)
        {
            ApplyLegacyCorners(window, hwnd);
            return;
        }

        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        if (window.TryFindResource("AppBorder") is not SolidColorBrush border) return;

        // COLORREF: 0x00BBGGRR, and the alpha byte is not an alpha — a non-zero one asks for the
        // system default colour instead of the one named here.
        var colorRef = border.Color.R | (border.Color.G << 8) | (border.Color.B << 16);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    /// <summary>
    /// Rounds the window by clipping it to a rounded rectangle, for the versions of Windows whose
    /// compositor will not round it on request.
    /// </summary>
    /// <remarks>
    /// The same GDI region every Windows 10-era application used for the same effect, and the only
    /// route left once DWMWA_WINDOW_CORNER_PREFERENCE is refused: the other one — AllowsTransparency,
    /// which would let WPF draw the curve itself the way 取詞翻譯 does — is the trade ApplyAppearance
    /// already turned down, and it would cost ClearType on every glyph in the window.
    ///
    /// It is a harder edge than the compositor’s. A region is a one-bit mask, so the curve is
    /// aliased where DWM’s is antialiased; that is the whole of what this costs, and it is what
    /// Windows 10 charged everybody else too.
    ///
    /// A maximised window gets no region at all. Windows draws those square, and rounding one only
    /// cuts notches out of the screen’s own corners.
    ///
    /// IsZoomed rather than the window’s WindowState: this runs from the WM_SIZE that announces the
    /// maximise, and hooks are called before WPF has read that message, so WindowState is still the
    /// state being left. The size is taken from GetWindowRect for the same kind of reason — a region
    /// is physical pixels measured from the window’s own top-left corner, which is exactly what that
    /// call gives and what device-independent units are not.
    /// </remarks>
    private static void ApplyLegacyCorners(Window window, IntPtr hwnd)
    {
        // Minimised, GetWindowRect answers with the icon's old off-screen rectangle rather than the
        // window's, and a region cut to that shape would be the one the window came back wearing for
        // as long as it took the restore's own WM_SIZE to arrive.
        if (IsIconic(hwnd)) return;

        if (IsZoomed(hwnd))
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        if (!GetWindowRect(hwnd, out var rect)) return;

        var width  = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0) return;

        // CreateRoundRectRgn’s last two arguments are the corner ellipse’s width and height, so a
        // radius of r is asked for as 2r. The rectangle excludes its right and bottom edge, hence
        // the +1 on each: without them the window loses its last column and row of pixels.
        var dpi = VisualTreeHelper.GetDpi(window);
        var diameter = (int)Math.Round(LegacyCornerRadius * 2 * dpi.DpiScaleX);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;

        // On success the window owns the region and will delete it itself, so it must not be
        // deleted here; on failure nothing else ever will, so it must.
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    /// <summary>
    /// Gives the window back the maximise, restore and minimise animations the system plays for
    /// every other window.
    /// </summary>
    /// <remarks>
    /// Those animations are DWM's, and DWM plays them only for a window whose style says it has a
    /// caption. WindowStyle="None" takes WS_CAPTION off, which is why this window used to snap
    /// between normal and maximised in a single frame while every other window on the desktop
    /// eased — the application looked like it had drawn the new size rather than moved to it.
    ///
    /// Putting the bit back does not put a caption back: WindowChrome answers WM_NCCALCSIZE by
    /// giving the client area the whole window, so there is no non-client strip left for the system
    /// to draw one in. The bit is read by DWM for the animation and by the shell for Snap Layouts;
    /// nothing draws from it.
    ///
    /// WS_CAPTION only, and never WS_THICKFRAME: that one is what makes a window resizable, and
    /// 更新視窗 is deliberately not. Whether a window can be resized stays WPF's decision, taken
    /// from ResizeMode.
    /// </remarks>
    private static void RestoreSystemAnimations(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_STYLE);
        if (style == 0 || (style & WS_CAPTION) == WS_CAPTION) return;

        SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION);
    }

    private static HwndSourceHook HookFor(Window window) =>
        (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            Hook(window, hwnd, msg, lParam, ref handled);

    private static IntPtr Hook(Window window, IntPtr hwnd, int msg, IntPtr lParam, ref bool handled)
    {
        // A region is a fixed set of pixels, so every new size needs a new one. WM_SIZE is where
        // they all arrive — a drag of the border, a maximise or restore, and the resize a move to a
        // monitor at another scale factor brings with it. Left unhandled: WPF needs it too.
        if (msg == WM_SIZE)
        {
            if (!DwmRoundsCorners) ApplyLegacyCorners(window, hwnd);
            return IntPtr.Zero;
        }

        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Relative to the monitor's own origin, which is what the message is expressed in — the
        // work area's absolute coordinates would place the window off a secondary monitor.
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;

        // Without this a maximised window cannot be dragged wider than the monitor it started on,
        // which is what the tracking size, not the maximised size, is for.
        mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, mmi.ptMaxSize.x);
        mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, mmi.ptMaxSize.y);

        // This message is also where WPF enforces MinWidth and MinHeight, and answering it takes
        // that away — a window with no floor can be dragged down to a title bar with nothing under
        // it. Same numbers, in the physical pixels this message is expressed in.
        var dpi = VisualTreeHelper.GetDpi(window);
        if (window.MinWidth > 0 && !double.IsInfinity(window.MinWidth))
            mmi.ptMinTrackSize.x = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX);
        if (window.MinHeight > 0 && !double.IsInfinity(window.MinHeight))
            mmi.ptMinTrackSize.y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY);

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
