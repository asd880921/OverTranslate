using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

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
            StartAutoClose();
        };
    }

    private void PositionWindow()
    {
        // Get DPI scale from this window's presentation source
        var src  = PresentationSource.FromVisual(this);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        if (_selPhysRect.HasValue)
        {
            var sel = _selPhysRect.Value;

            // Find the screen that contains the selection centre (physical pixels).
            // SystemParameters.WorkArea only covers the primary screen, so we must
            // resolve the correct screen explicitly for multi-monitor support.
            int centrePhysX = (int)(sel.Left + sel.Width  / 2);
            int centrePhysY = (int)(sel.Top  + sel.Height / 2);
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(centrePhysX, centrePhysY));
            var wa = screen.WorkingArea; // physical pixels of the target screen

            // Convert physical px → WPF DIP
            double selLeft = sel.Left    / dpiX;
            double selTop  = sel.Top     / dpiY;
            double selW    = sel.Width   / dpiX;
            double waLeft  = wa.Left     / dpiX;
            double waRight = wa.Right    / dpiX;
            double waTop   = wa.Top      / dpiY;

            // Horizontally centered over selection, clamped to this screen's work area
            double cx   = selLeft + selW / 2;
            double posX = Math.Clamp(cx - ActualWidth / 2, waLeft + 4, waRight - ActualWidth - 4);

            // Preferred: just above the selection
            double aboveY = selTop - ActualHeight - Gap;
            if (aboveY >= waTop)
            {
                Left = posX;
                Top  = aboveY;
            }
            else
            {
                // No room above → show at the top edge inside the selection
                Left = posX;
                Top  = selTop + Gap;
            }
        }
        else
        {
            // Fallback: bottom-right corner of primary screen
            var wa = SystemParameters.WorkArea;
            Left = wa.Right  - ActualWidth  - 16;
            Top  = wa.Bottom - ActualHeight - 16;
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
