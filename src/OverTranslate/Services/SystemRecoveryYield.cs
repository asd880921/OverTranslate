using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Lets the system's own recovery interface — Task Manager — come out above the capture layers,
/// and takes the top back once the user returns to this application.
/// </summary>
/// <remarks>
/// <para>The capture layers are Topmost and cover the whole virtual desktop, which means that when
/// something goes wrong behind them there is nothing the user can raise over them to deal with it.
/// Ctrl+Alt+Del still appears — winlogon draws it on its own desktop and no application can cover
/// that — but choosing 工作管理員 from it drops back to the ordinary desktop and starts Task Manager
/// there, underneath this application's Topmost windows. The user is then looking at the overlay
/// with the one tool that could end it hidden behind it.</para>
///
/// <para>The narrow answer, rather than dropping Topmost generally: Topmost is what keeps the
/// capture layer over the window being captured, and giving that up for every window would change
/// the feature during ordinary use. Only Task Manager sets it off, and returning to this
/// application puts it back. In between the layer is an ordinary window and anything can cover it —
/// which is the point, since by then the user has gone looking for a way to end the session.</para>
///
/// <para>This is not a substitute for <see cref="GlobalEscapeHook"/>. Both the foreground callback
/// and the Topmost change need the interface thread eventually — a thread wedged solid answers
/// neither, and Esc is what covers that. What this covers is the interface that is merely slow, or
/// misbehaving in a way that leaves it pumping: there the window the user reaches for actually
/// appears.</para>
///
/// <para>Task Manager only, matched by process name. Widening it — Process Explorer, another shell
/// tool — is a line in <see cref="RecoveryProcessNames"/>; it is deliberately not a guess about what
/// "system" means, because everything let past this can cover the screenshot the user is taking.</para>
/// </remarks>
internal sealed class SystemRecoveryYield : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Process names (no extension, as Windows reports them) allowed above the capture layer.</summary>
    private static readonly string[] RecoveryProcessNames = ["Taskmgr"];

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly WinEventProc _proc;
    private readonly Action _onChanged;

    // Its own thread for the same reason the Esc hook has one: a WinEvent hook is delivered to the
    // thread that installed it, and only while that thread pumps messages.
    private readonly HookThread _thread = new("OverTranslate recovery watch");

    private IntPtr _hookId;
    private int _yielded;         // 0/1 rather than bool: written on the hook thread, read on the dispatcher
    private IntPtr _recoveryWindow; // the window to sit directly behind while yielded

    /// <summary>True while Task Manager is the one that should be on top.</summary>
    public bool HasYielded => Volatile.Read(ref _yielded) != 0;

    /// <summary>
    /// The recovery window to sit behind, or zero. Leaving the topmost band is not enough on its
    /// own: it puts a window at the front of the ordinary band, which is still above the Task
    /// Manager the user just brought up. Measured — the layer stayed in front until it was
    /// explicitly placed behind this handle.
    /// </summary>
    public IntPtr RecoveryWindow => Volatile.Read(ref _recoveryWindow);

    private SystemRecoveryYield(Action onChanged)
    {
        _onChanged = onChanged;
        _proc = OnForegroundChanged;
    }

    /// <summary>
    /// Starts watching. <paramref name="onChanged"/> is raised on the dispatcher whenever
    /// <see cref="HasYielded"/> flips, and is expected to bring the session's windows into line.
    /// </summary>
    public static SystemRecoveryYield Install(Action onChanged)
    {
        var watch = new SystemRecoveryYield(onChanged);

        watch._thread.Start();
        watch._thread.Invoke(watch.InstallOnHookThread);

        return watch;
    }

    private void InstallOnHookThread()
    {
        // Foreground changes only: nothing here needs to know about anything else that moves, and
        // a wider range would put this callback on the path of events the whole desktop raises.
        _hookId = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);

        if (_hookId == IntPtr.Zero)
            Log.Warn("Foreground watch install failed — Task Manager will stay behind the capture layer");
    }

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;

        bool? yield = ClassifyForeground(hwnd);
        if (yield is not { } wanted) return; // some third application: leave the decision as it is

        Volatile.Write(ref _recoveryWindow, wanted ? hwnd : IntPtr.Zero);

        // Raised again even when the answer has not changed. Task Manager coming forward a second
        // time — from the taskbar, from Ctrl+Alt+Del, after its window was rebuilt — has to put the
        // layer behind whatever window it is now, and a handle from the first time it appeared may
        // no longer exist.
        bool changed = Interlocked.Exchange(ref _yielded, wanted ? 1 : 0) != (wanted ? 1 : 0);
        if (!changed && !wanted) return;

        if (changed)
            Log.Info("Capture layer {Action} the top for the system recovery interface", wanted ? "yields" : "takes back");

        // Send priority, and posted rather than run here: this is an OS callback on a hook thread,
        // and Topmost belongs to the thread that owns the windows.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Send, _onChanged);
    }

    /// <summary>
    /// True for the recovery interface, false for one of this application's own windows, and null
    /// for everything else — a third application coming forward is not an answer to this question,
    /// so whatever was decided last stands.
    /// </summary>
    private static bool? ClassifyForeground(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        if (pid == Environment.ProcessId) return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return IsRecoveryProcess(process.ProcessName) ? true : null;
        }
        catch (ArgumentException)
        {
            return null; // already gone between the event and this lookup
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether a process is one this application steps aside for.</summary>
    internal static bool IsRecoveryProcess(string processName) =>
        RecoveryProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _thread.Stop(() =>
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWinEvent(_hookId);
            _hookId = IntPtr.Zero;
        });

        Volatile.Write(ref _yielded, 0);
    }
}
