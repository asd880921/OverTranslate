using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// A private message-pumping thread for the low-level hooks to live on.
/// </summary>
/// <remarks>
/// <para>A low-level hook is delivered to the thread that installed it, and only while that thread
/// is pumping messages. Installing one from the dispatcher therefore ties every callback to the
/// interface being idle — and a callback that outstays <c>LowLevelHooksTimeout</c> (5s by default)
/// is silently dropped from the hook chain by Windows, after which that hook is dead for the rest
/// of the session and nothing says why.</para>
///
/// <para>Worse than losing the hook: Windows holds every keyboard and mouse event in the system
/// until the callback returns, so a busy UI thread delays the whole desktop's input for as long as
/// it is busy. That is exactly the moment Esc exists for, and exactly the moment a UI-thread hook
/// stops answering it.</para>
///
/// <para>Its own thread decouples the two. Nothing here runs the hook's <em>action</em> — the
/// callback should still hand that to the dispatcher and return at once.</para>
/// </remarks>
internal sealed class HookThread : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // How long each step of Stop waits. Bounded rather than infinite: a hook listener is not worth
    // hanging the application's close on.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly string _name;
    private Thread? _thread;
    private Dispatcher? _dispatcher;

    public HookThread(string name) => _name = name;

    /// <summary>The thread's dispatcher, or null while it is not running.</summary>
    public Dispatcher? Dispatcher => _dispatcher is { HasShutdownStarted: false } d ? d : null;

    /// <summary>Starts the thread if it is not already pumping. Safe to call repeatedly.</summary>
    public void Start()
    {
        if (Dispatcher is not null) return;

        // Waited on rather than assumed: the dispatcher belongs to the new thread and does not
        // exist until that thread reaches for it, and callers post work to it immediately.
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = _name,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        _thread = thread;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the hook thread and waits for it, or does nothing when the
    /// thread is not running.
    /// </summary>
    public void Invoke(Action work) => Dispatcher?.Invoke(work);

    /// <summary>
    /// Ends the thread, running <paramref name="teardown"/> on it first — hooks have to be removed
    /// from the thread that installed them.
    /// </summary>
    public void Stop(Action? teardown = null)
    {
        if (_dispatcher is not { } dispatcher) return;

        if (!dispatcher.HasShutdownStarted)
        {
            // Bounded, not a plain Invoke. This thread exists precisely because it can be sitting
            // inside an OS callback that nobody controls the length of, and waiting on it without a
            // limit would hang the application's close on exactly that — the hang this class is
            // here to keep off the interface thread in the first place.
            if (teardown is not null &&
                dispatcher.InvokeAsync(teardown).Wait(ShutdownTimeout) != DispatcherOperationStatus.Completed)
            {
                Log.Warn("Hook thread '{Name}' did not run its teardown within {Timeout}", _name, ShutdownTimeout);
            }

            dispatcher.InvokeShutdown();
        }

        // Said out loud rather than assumed: the fields are cleared either way, so a later Start
        // would quietly build a second thread beside a first one that is still pumping, and two
        // threads under the same name is not something anyone would work out from a stack list.
        if (_thread?.Join(ShutdownTimeout) == false)
            Log.Warn("Hook thread '{Name}' still running after {Timeout}", _name, ShutdownTimeout);

        _thread = null;
        _dispatcher = null;
    }

    public void Dispose() => Stop();
}
