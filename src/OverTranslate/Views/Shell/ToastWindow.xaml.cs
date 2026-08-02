using System.Windows;
using System.Windows.Media.Animation;

namespace OverTranslate.Views.Shell;

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
