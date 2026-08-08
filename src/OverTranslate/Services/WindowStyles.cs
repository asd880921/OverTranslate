using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverTranslate.Services;

/// <summary>
/// The extended window styles this application's floating layers need, in one place.
/// </summary>
/// <remarks>
/// Every overlay here wants some combination of the same three bits, and each one used to declare
/// its own copy of the two imports and the four constants. The cost of that was not the duplicated
/// lines: it was that "stop the realtime layers taking focus" looked like a one-file change in four
/// different files, so fixing it in the one that was reported left the other three as they were.
///
/// Call from <c>OnSourceInitialized</c> or later — before that the window has no handle and there is
/// nothing to set the style on.
/// </remarks>
internal static class WindowStyles
{
    private const int GWL_EXSTYLE = -20;

    /// <summary>Clicks pass through to whatever is underneath.</summary>
    private const int WS_EX_TRANSPARENT = 0x20;

    /// <summary>Required for WS_EX_TRANSPARENT to behave; also what AllowsTransparency renders into.</summary>
    private const int WS_EX_LAYERED = 0x80000;

    /// <summary>The window never takes activation, so showing it cannot pull focus off a game.</summary>
    private const int WS_EX_NOACTIVATE = 0x8000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    /// <summary>
    /// Makes the window unable to take focus while staying clickable. For chrome the user operates —
    /// a floating bar, an edit layer — which must not steal activation from the application it is
    /// sitting on top of.
    /// </summary>
    public static void ApplyNoActivate(Window window) => Add(window, WS_EX_NOACTIVATE);

    /// <summary>
    /// Makes the window invisible to the mouse entirely: clicks land on the application underneath.
    /// For layers that only ever display something.
    /// </summary>
    /// <param name="noActivate">
    /// Also refuse activation. A click-through window is rarely activated by the mouse anyway, but it
    /// can still be handed focus when it is shown — which over a full-screen game is exactly the
    /// thing not to do.
    /// </param>
    public static void ApplyClickThrough(Window window, bool noActivate = false)
    {
        var style = WS_EX_TRANSPARENT | WS_EX_LAYERED;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        Add(window, style);
    }

    /// <summary>
    /// Adds bits to the existing extended style rather than replacing it: WPF has already put its own
    /// there — WS_EX_TOOLWINDOW from ShowInTaskbar, WS_EX_LAYERED from AllowsTransparency — and
    /// overwriting them would undo settings made in XAML.
    /// </summary>
    private static void Add(Window window, int exStyle)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | exStyle);
    }
}
