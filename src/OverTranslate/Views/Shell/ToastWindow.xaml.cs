using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;

namespace OverTranslate.Views.Shell;

/// <summary>What kind of feedback a toast carries, which drives its status colour.</summary>
public enum ToastKind
{
    Info,
    Success,
    Error,
}

public partial class ToastWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int DisplayMs = 3000;
    private const int FadeMs    = 350;

    // Shorter than the timed fade: the reader has already decided, so the toast owes them an
    // acknowledgement rather than a farewell.
    private const int CloseFadeMs = 120;

    private const double Gap = 8;

    // At most one toast is on screen. A newer message replaces the older one outright instead of
    // stacking: these are momentary status reports, and the newest one is always the relevant one.
    private static ToastWindow? _current;

    // selPhysRect: selection bounds in physical screen pixels (same units as _lastSelPhys* in MainWindow)
    private readonly Rect? _selPhysRect;
    private DispatcherTimer? _autoCloseTimer;
    private bool _closed;
    private bool _fadingOut;

    // Set once the user has dismissed the toast, so the hover handlers stop reviving it. Without it,
    // a pointer that leaves and re-enters during the closing fade cancels that fade — and by then
    // the countdown is stopped and the toast is no longer the current one, so nothing would ever
    // close it again.
    private bool _userDismissed;

    /// <summary>Shows a toast, replacing whichever one is currently on screen.</summary>
    public static void Show(string title, string message, Rect? selPhysRect = null, ToastKind kind = ToastKind.Info)
    {
        Dismiss();

        var toast = new ToastWindow(title, message, selPhysRect, kind);
        _current = toast;
        ((Window)toast).Show();
    }

    /// <summary>
    /// Removes the toast currently on screen, if any. Called when a capture session ends: the toast
    /// is positioned against a selection that no longer exists, so leaving it to time out on its own
    /// strands it on screen with nothing to relate to.
    /// </summary>
    public static void Dismiss()
    {
        var existing = _current;
        _current = null;
        existing?.Close();
    }

    // Private on purpose: constructing one directly and calling the inherited Window.Show() would
    // bypass the single-toast bookkeeping, leaving two toasts stacked on the same spot.
    private ToastWindow(string title, string message, Rect? selPhysRect, ToastKind kind)
    {
        InitializeComponent();
        TitleText.Text   = title;
        MessageText.Text = message;
        _selPhysRect     = selPhysRect;

        // Resource reference rather than a resolved brush, so the bar follows a live theme switch.
        AccentBar.SetResourceReference(Shape.FillProperty, kind switch
        {
            ToastKind.Success => "AppSuccess",
            ToastKind.Error   => "AppError",
            _                 => "AppAccent",
        });

        Closed += (_, _) =>
        {
            _closed = true;
            _autoCloseTimer?.Stop();
            if (ReferenceEquals(_current, this))
                _current = null;
        };

        // Hovering holds the toast open. Without this the copy button is decorative: the countdown
        // would expire while the pointer is still on its way there, and the message it exists to
        // copy is exactly the kind (a failure with details) worth keeping.
        MouseEnter += (_, _) => PauseAutoClose();
        MouseLeave += (_, _) => ResumeAutoClose();

        // Start off-screen until Loaded gives us ActualHeight
        Left = -9999;
        Top  = -9999;

        // Nothing is shown until the position is settled: the first placement can still be moved by
        // WPF resizing the window on a DPI change, and watching the toast travel to its final spot
        // reads as a glitch.
        Opacity = 0;

        Loaded += (_, _) =>
        {
            PositionWindow();

            // Re-applied once the window has landed: crossing to a monitor at another scale makes
            // WPF resize it and Windows offer a replacement position, either of which undoes the
            // alignment just made.
            Dispatcher.BeginInvoke(
                new Action(() => { PositionWindow(); Opacity = 1; }), DispatcherPriority.Loaded);

            StartAutoClose();
        };
    }

    private void PositionWindow()
    {
        // All physical pixels, scaled by the monitor being placed on. Reading the scale off this
        // window instead reports the monitor it currently sits on — which until this runs is the
        // one holding the off-screen parking position, not the one it is headed for.
        if (_selPhysRect.HasValue)
        {
            var sel = _selPhysRect.Value;

            // SystemParameters.WorkArea only covers the primary screen, so the target screen has to
            // be resolved explicitly for multi-monitor support.
            int centreX = (int)(sel.Left + sel.Width  / 2);
            int centreY = (int)(sel.Top  + sel.Height / 2);
            var wa = System.Windows.Forms.Screen
                .FromPoint(new System.Drawing.Point(centreX, centreY)).WorkingArea;
            double scale = ScreenGeometry.ScaleAt(centreX, centreY);

            double w   = ActualWidth  * scale;
            double h   = ActualHeight * scale;
            double gap = Gap * scale;

            // Math.Clamp throws when the toast is wider than the monitor it must fit on.
            double minX = wa.Left + 4 * scale;
            double maxX = Math.Max(minX, wa.Right - w - 4 * scale);
            double posX = Math.Clamp(sel.Left + sel.Width / 2 - w / 2, minX, maxX);

            // Preferred just above the selection; otherwise at its top edge, inside it.
            double aboveY = sel.Top - h - gap;
            double posY   = aboveY >= wa.Top ? aboveY : sel.Top + gap;

            ScreenGeometry.MoveToPhysical(this, (int)Math.Round(posX), (int)Math.Round(posY));
        }
        else
        {
            // Fallback: bottom-right corner of the primary screen
            var wa = (System.Windows.Forms.Screen.PrimaryScreen
                      ?? System.Windows.Forms.Screen.AllScreens[0]).WorkingArea;
            double scale = ScreenGeometry.ScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
            ScreenGeometry.MoveToPhysical(this,
                (int)Math.Round(wa.Right  - ActualWidth  * scale - 16 * scale),
                (int)Math.Round(wa.Bottom - ActualHeight * scale - 16 * scale));
        }
    }

    // A timer rather than an awaited delay, because the countdown has to be stoppable on hover.
    private void StartAutoClose()
    {
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisplayMs) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer!.Stop();
            FadeOutAndClose();
        };
        _autoCloseTimer.Start();
    }

    private void PauseAutoClose()
    {
        if (_closed || _userDismissed) return;

        _autoCloseTimer?.Stop();

        // Cancels a fade already in flight. The flag is what actually calls it off: removing the
        // animation does not reliably suppress its Completed handler, so without this the toast
        // would close under a pointer that is resting on it to read.
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    // Restarts the full countdown rather than resuming the remainder: the pointer leaving means the
    // reader just finished, and giving them the leftover 200ms of a spent timer reads as a glitch.
    private void ResumeAutoClose()
    {
        if (_closed || _userDismissed) return;
        _autoCloseTimer?.Start();
    }

    private void FadeOutAndClose(int durationMs = FadeMs)
    {
        if (_closed) return;

        // Windows' "animation effects" setting is the local equivalent of a reduced-motion
        // preference; a fade is motion the user has asked not to be shown.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        _fadingOut = true;
        var fade = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(durationMs));
        fade.Completed += (_, _) => { if (_fadingOut) Close(); };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <remarks>
    /// The clipboard belongs to whatever else is running, and any of it can hold the clipboard open
    /// long enough for this to fail — WPF already retries for about a second before throwing. Left
    /// unhandled that throw reaches the dispatcher and takes the whole app down, over a copy button
    /// on a toast the reader was about to dismiss anyway.
    /// </remarks>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = $"{TitleText.Text}\n{MessageText.Text}";
        var copied = TryCopy(text);

        // Confirm in place: the toast is about to be dismissed by the pointer leaving, so a second
        // toast announcing the copy would replace this one and lose the message just copied. On a
        // failure the message is still on screen above this line, which is where the reader gets it
        // from instead.
        ShowCopyHint(
            copied ? "S.Toast.Copied" : "S.Toast.CopyFailed",
            copied ? "AppSuccess" : "AppError");
    }

    /// <remarks>
    /// A throw does not settle whether the text was copied. SetText publishes it and then flushes
    /// it so it outlives this process, and the flush is the half that fails most often — which
    /// leaves the text on the clipboard, pasteable until OverTranslate exits. Telling the reader it
    /// failed when their next paste would have worked is the worse of the two mistakes, so ask the
    /// clipboard what actually happened rather than inferring it from the exception.
    /// </remarks>
    private static bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not copy the toast message to the clipboard");
        }

        try
        {
            return System.Windows.Clipboard.ContainsText()
                && System.Windows.Clipboard.GetText() == text;
        }
        catch (Exception ex)
        {
            // Whatever holds the clipboard shut holds it shut both ways. Unreadable is not the same
            // as absent, but from here the two are indistinguishable and the safe answer is the one
            // that leaves the message on screen.
            Log.Trace(ex, "Could not read the clipboard back after a failed copy");
            return false;
        }
    }

    // Resource references rather than literals so the line follows a language or theme change the
    // same way the rest of the toast does.
    private void ShowCopyHint(string textKey, string brushKey)
    {
        CopyHint.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, textKey);
        CopyHint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
        CopyHint.Visibility = Visibility.Visible;
    }

    // Hovering has already stopped the countdown, so without this the only way out of a toast the
    // reader is done with is to move the pointer away and wait.
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_current, this))
            _current = null;
        _userDismissed = true;
        _autoCloseTimer?.Stop();
        FadeOutAndClose(CloseFadeMs);
    }
}
