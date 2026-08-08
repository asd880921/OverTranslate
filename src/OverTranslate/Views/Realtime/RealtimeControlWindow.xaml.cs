using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OverTranslate.Views.Realtime;

public enum RealtimeControlMode
{
    Edit,
    Running
}

/// <summary>
/// Whether a bar message is answering the user or reporting that something went wrong. Only the
/// second colours the status dot, so the distinction has to be made by the caller — a message that
/// merely reads badly is not a failure.
/// </summary>
public enum RealtimeMessageKind
{
    Info,
    Failure
}

/// <summary>
/// The one piece of chrome that stays on screen for the whole realtime session: a bar while the user
/// is framing blocks, a capsule once translation is running. Draggable, so it can always be moved
/// off whatever it happens to be covering.
/// </summary>
/// <remarks>
/// It is one window across both modes rather than two, so its position survives the switch — a user
/// who moved it out of the way of a subtitle should not find it back in the middle after pressing
/// 編輯. Like the other realtime windows it never takes activation and is excluded from screen
/// capture, so it can neither pull focus from a full-screen game nor end up inside a watched block's
/// own grab.
/// </remarks>
public partial class RealtimeControlWindow : Window
{
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(2.6);

    private readonly System.Drawing.Rectangle _screenBounds;
    private readonly DispatcherTimer _messageTimer;

    private RealtimeControlMode _mode = RealtimeControlMode.Edit;
    private string _editHint = "在螢幕上拖曳建立要翻譯的區塊";
    // What the capsule says before SetLanguages has named the pair. A transient message borrows the
    // same slot and RestoreText brings whichever of the two applies back.
    private readonly string _runStatus = "即時翻譯中";
    private bool _hasLanguages;

    // The scale WPF renders this window at, which is the DPI of whatever monitor it was created on —
    // not necessarily the one it is pinned to. Everything that converts between the window's DIP and
    // screen pixels has to use this one.
    private double _windowScale = 1.0;

    private System.Drawing.Point _position;
    private bool _isDragging;
    private System.Drawing.Point _dragStartMouse;
    private System.Drawing.Point _dragStartPosition;

    public RealtimeControlWindow(System.Drawing.Rectangle screenBounds)
    {
        InitializeComponent();

        _screenBounds = screenBounds;
        _messageTimer = new DispatcherTimer { Interval = MessageDuration };
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); RestoreText(); };

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
                _windowScale = target.TransformToDevice.M11;

            ApplyScreenScale();
            PlaceInitially();
        };

        // The bar is sized to its content, so every change of text changes its width — a status
        // message replacing another, a block count going from one digit to two. Left where it is,
        // the left edge stays put and the right edge walks in and out, which is the movement the eye
        // notices. Growing from the middle instead keeps the bar where it was and makes the change
        // read as the text changing rather than the bar moving.
        SizeChanged += (_, e) =>
        {
            if (!IsLoaded || !e.WidthChanged || _isDragging) return;

            var grown = (int)Math.Round((e.NewSize.Width - e.PreviousSize.Width) * _windowScale);
            if (grown == 0) return;

            _position = _position with { X = _position.X - grown / 2 };
            ClampIntoScreen();
        };
    }

    public event EventHandler? StartRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? ShotRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowStyles.ApplyNoActivate(this);
        WindowCaptureShield.Exclude(this);
    }

    /// <summary>
    /// Puts the session's language pair where the running capsule's status text was.
    /// </summary>
    /// <remarks>
    /// The pulsing dot beside it already says the session is running, so "即時翻譯中" was a second
    /// way of saying the same thing, occupying the one line the user can see while the translation
    /// window is hidden. The pair is the thing that cannot be checked anywhere else and the one
    /// setting that fails silently when it is wrong: recognition told to read the wrong script does
    /// not raise an error, it returns plausible nonsense, and this is where that is caught at a glance.
    /// </remarks>
    public void SetLanguages(string sourceCode, string targetCode)
    {
        SourceLangText.Text = Models.LanguageData.GetSourceName(sourceCode);
        TargetLangText.Text = Models.LanguageData.GetTargetName(targetCode);
        _hasLanguages = SourceLangText.Text.Length > 0 && TargetLangText.Text.Length > 0;

        if (!_messageTimer.IsEnabled) RestoreText();
    }

    public void SetMode(RealtimeControlMode mode)
    {
        _mode = mode;
        EditPanel.Visibility = mode == RealtimeControlMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        RunPanel.Visibility = mode == RealtimeControlMode.Running ? Visibility.Visible : Visibility.Collapsed;

        _messageTimer.Stop();
        RestoreText();
        SetBusy(false);

        // The capsule is far narrower than the bar, so the window's size changes under a position
        // that was valid for the other mode.
        Dispatcher.BeginInvoke(new Action(ClampIntoScreen), DispatcherPriority.Loaded);
    }

    public void SetBlockCount(int count, int max) => BlockCountText.Text = $"· {count}/{max}";

    /// <summary>Pulses the status dot while a recognition or translation pass is in flight.</summary>
    public void SetBusy(bool busy)
    {
        if (busy)
        {
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 1,
                To = 0.3,
                Duration = new Duration(TimeSpan.FromMilliseconds(620)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            return;
        }

        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    /// <summary>
    /// Replaces the bar's own text for a moment. Transient by design: these are answers to something
    /// the user just did (a refused drag, an engine that failed), not state worth keeping on screen.
    /// </summary>
    public void ShowMessage(string message, RealtimeMessageKind kind = RealtimeMessageKind.Info)
    {
        if (_mode == RealtimeControlMode.Edit)
        {
            EditHintText.Text = message;
        }
        else
        {
            // A message is a sentence, not a pair, so it takes the whole slot back for its duration.
            LangPairPanel.Visibility = Visibility.Collapsed;
            RunStatusText.Visibility = Visibility.Visible;
            RunStatusText.Text = message;

            // Same frame as the text it belongs to. The dot is the one thing on this bar visible at
            // a glance from across a full-screen window, so a failure is worth spending its colour
            // on; it goes back to accent when the message does.
            SetDotFailed(kind == RealtimeMessageKind.Failure);
        }

        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>Re-asserts the bar above a window created after it — the edit layer, on re-entry.</summary>
    public void BringToFront()
    {
        Topmost = false;
        Topmost = true;
    }

    private void RestoreText()
    {
        EditHintText.Text = _editHint;
        RunStatusText.Text = _runStatus;

        LangPairPanel.Visibility = _hasLanguages ? Visibility.Visible : Visibility.Collapsed;
        RunStatusText.Visibility = _hasLanguages ? Visibility.Collapsed : Visibility.Visible;

        SetDotFailed(false);
    }

    // SetResourceReference rather than an assigned brush: the theme can still be switched from the
    // shell while a session runs, and a snapshotted brush would keep the old theme's red.
    private void SetDotFailed(bool failed) =>
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            failed ? "AppError" : "AppAccent");

    // ── Placement and dragging ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the bar for the monitor it will sit on rather than the one WPF measured it against.
    /// The two are the same on a uniform desktop, so this is a no-op there; on a mixed-DPI one it is
    /// the difference between a bar at the right physical size and one at another monitor's scale.
    /// </summary>
    private void ApplyScreenScale()
    {
        double targetScale = ScreenGeometry.ScaleAt(
            _screenBounds.Left + _screenBounds.Width / 2,
            _screenBounds.Top + _screenBounds.Height / 2);

        double relative = targetScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);
    }

    private void PlaceInitially()
    {
        UpdateLayout();

        double targetScale = ScreenGeometry.ScaleAt(
            _screenBounds.Left + _screenBounds.Width / 2,
            _screenBounds.Top + _screenBounds.Height / 2);

        // Bottom centre, which does sit where subtitles do — the top edge was tried and read as
        // out of the way to the point of being out of mind, and a control the user forgets is there
        // is worse than one they move once. Dragging is the answer to the overlap, and the position
        // survives switching modes so it only has to be done once a session. The 48px gap is scaled
        // by the target monitor so it reads the same distance up on every display.
        _position = new System.Drawing.Point(
            _screenBounds.Left + (_screenBounds.Width - PhysicalWidth) / 2,
            _screenBounds.Bottom - PhysicalHeight - (int)Math.Round(48 * targetScale));

        ClampIntoScreen();
    }

    private void ClampIntoScreen()
    {
        int x = Math.Clamp(
            _position.X, _screenBounds.Left, Math.Max(_screenBounds.Left, _screenBounds.Right - PhysicalWidth));
        int y = Math.Clamp(
            _position.Y, _screenBounds.Top, Math.Max(_screenBounds.Top, _screenBounds.Bottom - PhysicalHeight));

        _position = new System.Drawing.Point(x, y);
        ScreenGeometry.MoveToPhysical(this, x, y);
    }

    // ActualWidth already includes the LayoutTransform above, so the window's own render scale is
    // all that stands between it and screen pixels.
    private int PhysicalWidth => (int)Math.Round(ActualWidth * _windowScale);
    private int PhysicalHeight => (int)Math.Round(ActualHeight * _windowScale);

    // Physical pixels throughout, taken from the cursor rather than from WPF: the window carries its
    // own position in screen pixels, and mixing in a DIP delta would drift on a scaled display.
    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartMouse = System.Windows.Forms.Control.MousePosition;
        _dragStartPosition = _position;
        ((UIElement)sender).CaptureMouse();
    }

    private void Chrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var mouse = System.Windows.Forms.Control.MousePosition;
        _position = new System.Drawing.Point(
            _dragStartPosition.X + (mouse.X - _dragStartMouse.X),
            _dragStartPosition.Y + (mouse.Y - _dragStartMouse.Y));
        ClampIntoScreen();
    }

    private void Chrome_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void EditBtn_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private void ShotBtn_Click(object sender, RoutedEventArgs e) => ShotRequested?.Invoke(this, EventArgs.Empty);

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
