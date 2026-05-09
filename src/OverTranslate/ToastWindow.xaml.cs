using System.Windows;
using System.Windows.Media.Animation;

namespace OverTranslate;

public partial class ToastWindow : Window
{
    private const int DisplayMs = 2500;
    private const int FadeMs    = 350;
    private const double Gap = 8;

    // selPhysRect: selection bounds in physical screen pixels (same units as _lastSelPhys* in MainWindow)
    private readonly Rect? _selPhysRect;

    public ToastWindow(string title, string message, Rect? selPhysRect = null)
    {
        InitializeComponent();
        TitleText.Text   = title;
        MessageText.Text = message;
        _selPhysRect     = selPhysRect;

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

            // Convert physical px → WPF DIP
            double selLeft = sel.Left   / dpiX;
            double selTop  = sel.Top    / dpiY;
            double selW    = sel.Width  / dpiX;

            var wa = SystemParameters.WorkArea;

            // Horizontally centered over selection, clamped to work area
            double cx   = selLeft + selW / 2;
            double posX = Math.Clamp(cx - ActualWidth / 2, wa.Left + 4, wa.Right - ActualWidth - 4);

            // Preferred: just above the selection
            double aboveY = selTop - ActualHeight - Gap;
            if (aboveY >= wa.Top)
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
            // Fallback: bottom-right corner
            var wa = SystemParameters.WorkArea;
            Left = wa.Right  - ActualWidth  - 16;
            Top  = wa.Bottom - ActualHeight - 16;
        }
    }

    private async void StartAutoClose()
    {
        await Task.Delay(DisplayMs);
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(FadeMs));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText($"{TitleText.Text}\n{MessageText.Text}");
    }
}
