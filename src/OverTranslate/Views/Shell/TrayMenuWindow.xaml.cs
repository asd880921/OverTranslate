using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using OverTranslate.Layout;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Shell;

public partial class TrayMenuWindow : Window
{
    private const double EdgeGap = 4;
    private const double ShadowInset = 18;

    public event EventHandler? OpenTranslationRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    private readonly System.Drawing.Point _cursorPhys;
    private bool _dismissed;

    public TrayMenuWindow()
    {
        InitializeComponent();
        _cursorPhys = System.Windows.Forms.Cursor.Position;

        // Start off-screen; Loaded repositions after ActualWidth/Height are measured
        Left = -9999;
        Top = -9999;

        Loaded += (_, _) =>
        {
            PositionWindow();
            Activate();
        };
        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        PreviewMouseDown += DismissOnNonButtonClick;
    }

    /// <remarks>
    /// The monitor's full bounds, deliberately not its work area. The work area is the screen minus
    /// the taskbar, and the tray icon this menu belongs to is <i>on</i> the taskbar — so measured
    /// against the work area the menu can never reach the thing that opened it. Its corner stops at
    /// the taskbar's top edge and the menu reads as belonging to nothing, which is exactly what it
    /// looked like for an icon dragged out of the overflow onto the taskbar. An icon inside the
    /// overflow flyout sits in the work area instead, which is why the same menu looked correct
    /// there and hid the problem.
    ///
    /// Crossing the taskbar is safe here: the window is topmost and activated, so it is drawn above
    /// the taskbar rather than clipped by it (verified — a point inside the overlap belongs to this
    /// window, not to Shell_TrayWnd), and it is gone on the next click either way. Bounds still
    /// keeps the whole menu on the monitor, which is what the clamping is really for.
    /// </remarks>
    private void PositionWindow()
    {
        var area = System.Windows.Forms.Screen.FromPoint(_cursorPhys).Bounds;
        var scale = ScreenGeometry.ScaleAt(_cursorPhys.X, _cursorPhys.Y);
        var (left, top) = TrayMenuPlacement.Place(
            new Point(_cursorPhys.X, _cursorPhys.Y),
            new Size(ActualWidth * scale, ActualHeight * scale),
            new Rect(area.Left, area.Top, area.Width, area.Height),
            EdgeGap * scale,
            ShadowInset * scale);

        ScreenGeometry.MoveToPhysical(this, left, top);
    }

    private void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        Close();
    }

    private void DismissOnNonButtonClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;

        Dismiss();
    }

    internal static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;

            DependencyObject? visualParent = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : null;
            element = visualParent ?? LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>
    /// Relabels the first item for what it will actually do, which depends on whether a realtime
    /// session owns the screen.
    /// </summary>
    /// <remarks>
    /// The translation window cannot be used during a session — the layers cover the screen — so
    /// the item is spent on the thing a user opening this menu mid-session is likely to want: a
    /// control bar that something else has covered, put back in reach. Saying so on the item is the
    /// point of doing it here as well as on the icon's left click, which is the same action with
    /// nothing to explain it.
    /// </remarks>
    // Segoe Fluent Icons, spelled out rather than pasted: the glyphs are private-use characters
    // that show as nothing in most editors and diffs.
    private const string WindowIcon = "\uE737";   // window
    private const string PinIcon = "\uE8A7";      // pin to top

    public void SetRealtimeRunning(bool running)
    {
        OpenWindowLabel.Text = LocalizationService.Get(
            running ? "S.Tray.RealtimeWindow" : "S.Tray.OpenWindow");
        OpenWindowIcon.Text = running ? PinIcon : WindowIcon;
    }

    private void OpenWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenTranslationRequested?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    private void ExitBtn_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }
}
