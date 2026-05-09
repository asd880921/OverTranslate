using System.Windows;
using System.Windows.Input;

namespace OverTranslate;

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
