using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Where on the screen the user's selection is, when anyone will say.
/// </summary>
/// <remarks>
/// 快速翻譯 puts a small hint over the text it is replacing, and text is where the user is looking —
/// a hint in the corner of the screen would be a message about their selection placed as far from it
/// as the desktop allows.
///
/// Nobody has to answer, and most applications do not. Two sources are asked, in order of how much
/// they know:
///
/// UI Automation's TextPattern is the one that actually knows where the selected characters are, and
/// it is implemented by the applications built out of text — Office, Notepad, the editors, anything
/// hosting a standard text control. It is not implemented by Chrome's page content without
/// accessibility switched on, by most games, or by anything drawing its own glyphs.
///
/// The caret is the fallback. An application with a caret has one end of the selection in a place
/// Windows already knows, which is not the selection but is within a line of it.
///
/// Then nothing, and nothing is an ordinary answer: the caller falls back to the pointer, which is
/// where the user's hand is and therefore never a bad guess. See <see cref="Layout.HintPlacement"/>.
///
/// Physical screen pixels throughout, which is what both sources report to a per-monitor DPI aware
/// process and what the windows are placed with.
/// </remarks>
internal static class SelectionBounds
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// How long the probe may take before the hint goes to the pointer instead.
    /// </summary>
    /// <remarks>
    /// UI Automation is a cross-process call into an application that may be busy, hung, or hostile
    /// to being asked, and this runs on the path between the user pressing a shortcut and seeing
    /// anything happen. A worse position now beats a better one after a visible pause.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    /// <summary>Locates the selection, or returns null when nothing will say where it is.</summary>
    public static async Task<Rect?> LocateAsync()
    {
        // Off the UI thread and abandoned rather than cancelled: a cross-process automation call
        // cannot be interrupted, so the budget is about when this stops waiting, not when the call
        // stops running.
        var probe = Task.Run(Locate);

        if (await Task.WhenAny(probe, Task.Delay(Budget)) != probe)
        {
            Log.Debug("The selection could not be located within {Budget}ms; the pointer is used instead",
                Budget.TotalMilliseconds);
            return null;
        }

        return await probe;
    }

    private static Rect? Locate()
    {
        try
        {
            return FromTextPattern() ?? FromCaret();
        }
        catch (Exception ex)
        {
            // Every source here is an application answering about its own state, and any of them may
            // be gone by the time it is asked. Not knowing where the selection is costs the hint a
            // better position and nothing else.
            Log.Debug(ex, "The selection could not be located; the pointer is used instead");
            return null;
        }
    }

    /// <remarks>
    /// The union of every rectangle in the selection: a selection that spans lines is reported as
    /// one rectangle per line, and what the caller wants is the block they are all part of.
    /// </remarks>
    private static Rect? FromTextPattern()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null) return null;

        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ||
            pattern is not TextPattern text)
            return null;

        var union = Rect.Empty;

        foreach (var range in text.GetSelection())
            foreach (var rectangle in range.GetBoundingRectangles())
                if (rectangle.Width > 0 || rectangle.Height > 0)
                    union.Union(rectangle);

        return IsUsable(union) ? union : null;
    }

    /// <remarks>
    /// The caret belongs to the thread that owns the foreground window, and its rectangle is in the
    /// client coordinates of whichever window is drawing it — neither of which is anything this
    /// process can assume, so both are asked for rather than guessed at.
    /// </remarks>
    private static Rect? FromCaret()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0) return null;

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero) return null;

        var topLeft = new POINT { x = info.rcCaret.left, y = info.rcCaret.top };
        var bottomRight = new POINT { x = info.rcCaret.right, y = info.rcCaret.bottom };
        if (!ClientToScreen(info.hwndCaret, ref topLeft) ||
            !ClientToScreen(info.hwndCaret, ref bottomRight))
            return null;

        var caret = new Rect(
            topLeft.x,
            topLeft.y,
            Math.Max(0, bottomRight.x - topLeft.x),
            Math.Max(0, bottomRight.y - topLeft.y));

        return IsUsable(caret) ? caret : null;
    }

    /// <summary>
    /// Whether a reported rectangle is somewhere a hint could be put.
    /// </summary>
    /// <remarks>
    /// A caret is a line with no width and a collapsed selection can be reported the same way, so
    /// zero width is a position rather than a refusal. What is refused is the empty rectangle, and
    /// the off-screen coordinates applications use to park a caret they are not drawing.
    /// </remarks>
    private static bool IsUsable(Rect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.Height <= 0) return false;

        var desktop = ScreenGeometry.VirtualDesktopBounds();
        return rectangle.Right > desktop.Left && rectangle.Left < desktop.Right &&
               rectangle.Bottom > desktop.Top && rectangle.Top < desktop.Bottom;
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
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
