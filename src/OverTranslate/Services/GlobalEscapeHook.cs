using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

// A process-wide low-level keyboard hook that swallows Esc and reports it to a callback.
//
// This belongs to the capture session rather than to any single window. Before a selection
// exists there is no overlay to host a hook, and ScreenCaptureWindow.OnKeyDown only fires while
// that window holds the keyboard focus — which it cannot count on (the tray menu closing right
// after it opens, full-screen games, elevated foreground windows). Owning the hook for the whole
// session is what makes Esc behave identically before and after the selection is drawn.
internal sealed class GlobalEscapeHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onEscape;
    private IntPtr _hookId;

    private GlobalEscapeHook(Action onEscape)
    {
        _onEscape = onEscape;
        _proc = HookCallback;
    }

    public static GlobalEscapeHook Install(Action onEscape)
    {
        var hook = new GlobalEscapeHook(onEscape);

        // GetModuleHandle(null) returns this process's own module handle directly. Resolving it via
        // Process.GetCurrentProcess().MainModule enumerates the process module list and costs
        // milliseconds on a path that runs while the capture window is being presented.
        hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);

        if (hook._hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn("Esc hook install failed (win32 error {Error}) — Esc will only cancel while the capture window has focus",
                error);
        }

        return hook;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && Marshal.ReadInt32(lParam) == VK_ESCAPE)
        {
            // Post, never wait. A low-level hook callback that runs longer than
            // LowLevelHooksTimeout (5s by default) gets silently dropped from the hook chain by
            // Windows, and from then on Esc would stop cancelling for the rest of the session.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(_onEscape);
            return (IntPtr)1; // swallow, so Esc does not also reach the app being translated
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }
}
