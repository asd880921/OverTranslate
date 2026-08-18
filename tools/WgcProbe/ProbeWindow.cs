using System.Drawing;
using System.Windows;
using System.Windows.Media;

namespace WgcProbe;

/// <summary>
/// The two windows the probe's experiments are run against: a stand-in for the application being
/// watched, and a stand-in for a realtime subtitle layer.
/// </summary>
/// <remarks>
/// Both are the probe's own so an experiment cannot be moved out from under itself by whatever the
/// user is doing on their desktop — the first attempt at the overlay test measured a browser window
/// that was dragged to another monitor mid-run.
///
/// The subtitle layer is built to the same recipe as <c>RealtimeBlockWindow</c>: WPF
/// <c>AllowsTransparency</c>, topmost, not activating, click-through. That recipe is the whole
/// subject of #94 — it is what makes <c>WDA_EXCLUDEFROMCAPTURE</c> fail — so a stand-in built any
/// other way would be testing a window this application does not have.
/// </remarks>
internal static class ProbeWindow
{
    /// <summary>The subtitle layer's fill. Nothing on a real screen is this colour.</summary>
    public static readonly System.Drawing.Color Marker = System.Drawing.Color.FromArgb(255, 255, 0, 255);

    /// <summary>
    /// Shows one window and returns once it has painted. Disposing the return value closes it.
    /// </summary>
    /// <param name="opaqueWhite">The source window if true, the subtitle layer otherwise.</param>
    public static IDisposable Show(Rectangle bounds, bool opaqueWhite, out IntPtr hwnd)
    {
        var ready = new ManualResetEventSlim(false);
        var handle = IntPtr.Zero;
        System.Windows.Threading.Dispatcher? dispatcher = null;

        // Its own STA thread with its own dispatcher, because the thread that asked for it is about
        // to block on capturing.
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = !opaqueWhite,
                Background = new SolidColorBrush(opaqueWhite
                    ? Colors.White
                    : System.Windows.Media.Color.FromArgb(255, Marker.R, Marker.G, Marker.B)),
                Topmost = true,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                // Content, so ContentRendered actually fires and so the source window has something
                // in it that a capture can be seen to have caught.
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = opaqueWhite ? "source window" : "subtitle layer",
                    FontSize = 32,
                    Foreground = new SolidColorBrush(Colors.Black),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            window.SourceInitialized += (_, _) =>
            {
                handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Native.PinPhysicalBounds(handle, bounds);
                if (!opaqueWhite) Native.MakeClickThrough(handle);
            };

            window.ContentRendered += (_, _) => ready.Set();
            window.Show();

            System.Windows.Threading.Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            Console.Error.WriteLine("warning: a probe window did not report itself rendered");

        // The compositor needs a moment past ContentRendered before the pixels are on the glass.
        Thread.Sleep(400);

        hwnd = handle;
        return new Closer(() => dispatcher?.InvokeShutdown());
    }

    private sealed class Closer(Action close) : IDisposable
    {
        public void Dispose() => close();
    }
}
