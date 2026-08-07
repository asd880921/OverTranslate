using System.Windows;
using System.Windows.Input;

namespace OverTranslate.Views.Shell;

public partial class TrayMenuWindow : Window
{
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
        Top  = -9999;

        Loaded      += (_, _) => PositionWindow();
        Deactivated += (_, _) => Dismiss();
        KeyDown     += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
    }

    private void PositionWindow()
    {
        var src  = PresentationSource.FromVisual(this);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // Convert physical cursor coords → WPF DIPs
        double cx = _cursorPhys.X / dpiX;
        double cy = _cursorPhys.Y / dpiY;

        var wa = SystemParameters.WorkArea;

        // Default: align left edge with cursor, bottom edge with cursor (appears above)
        double left = cx;
        double top  = cy - ActualHeight;

        if (left + ActualWidth > wa.Right) left = wa.Right - ActualWidth - 4;
        if (left < wa.Left)                left = wa.Left  + 4;
        if (top  < wa.Top)                 top  = cy + 4;  // no room above → show below

        Left = left;
        Top  = top;
    }

    private void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        Close();
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
        OpenWindowLabel.Text = running ? "將即時翻譯視窗移至最上層" : "開啟翻譯視窗";
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
