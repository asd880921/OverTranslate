using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x8000000;

    private readonly System.Drawing.Rectangle _screenBounds;
    private readonly DispatcherTimer _messageTimer;

    private RealtimeControlMode _mode = RealtimeControlMode.Edit;
    private string _editHint = "在螢幕上拖曳建立要翻譯的區塊";
    private string _runStatus = "即時翻譯中";

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
    }

    public event EventHandler? StartRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);

        WindowCaptureShield.Exclude(this);
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
    public void ShowMessage(string message)
    {
        if (_mode == RealtimeControlMode.Edit) EditHintText.Text = message;
        else RunStatusText.Text = message;

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
    }

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

        // Bottom centre: out of the way of the content in the middle of the screen, and the place a
        // transient control is expected to be. The user can drag it anywhere from there. The 48px
        // gap is scaled by the target monitor so it reads the same distance up on every display.
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

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
