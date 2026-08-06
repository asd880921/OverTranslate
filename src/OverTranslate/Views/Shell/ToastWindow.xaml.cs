using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private const int DisplayMs = 5000;
    private const int FadeMs    = 350;
    private const double Gap = 8;

    // At most one toast is on screen. A newer message replaces the older one outright instead of
    // stacking: these are momentary status reports, and the newest one is always the relevant one.
    private static ToastWindow? _current;

    // selPhysRect: selection bounds in physical screen pixels (same units as _lastSelPhys* in MainWindow)
    private readonly Rect? _selPhysRect;
    private DispatcherTimer? _autoCloseTimer;
    private bool _closed;

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

        Loaded += (_, _) =>
        {
            PositionWindow();

            // Re-applied once the window has landed: crossing to a monitor at another scale makes
            // WPF resize it and Windows offer a replacement position, either of which undoes the
            // alignment just made. Same inputs, so it is a no-op on a uniform desktop.
            Dispatcher.BeginInvoke(new Action(PositionWindow), DispatcherPriority.Loaded);

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
        if (_closed) return;

        _autoCloseTimer?.Stop();

        // The pointer may have arrived mid-fade; clearing the animation hands Opacity back so it
        // can be restored, otherwise the animated value would keep overriding the assignment.
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    // Restarts the full countdown rather than resuming the remainder: the pointer leaving means the
    // reader just finished, and giving them the leftover 200ms of a spent timer reads as a glitch.
    private void ResumeAutoClose()
    {
        if (_closed) return;
        _autoCloseTimer?.Start();
    }

    private void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(FadeMs));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText($"{TitleText.Text}\n{MessageText.Text}");

        // Confirm in place: the toast is about to be dismissed by the pointer leaving, so a second
        // toast announcing the copy would replace this one and lose the message just copied.
        CopyHint.Visibility = Visibility.Visible;
    }
}
