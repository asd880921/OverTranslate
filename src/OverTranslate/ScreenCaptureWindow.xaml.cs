using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WPoint = System.Windows.Point;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace OverTranslate;

public partial class ScreenCaptureWindow : Window
{
    private const int WM_NCHITTEST  = 0x0084;
    private const int HTTRANSPARENT = -1;

    private HwndSource? _hwndSource;
    private bool _inBackgroundMode;

    private readonly Bitmap _screenshot;
    private readonly System.Drawing.Rectangle _physBounds;
    private readonly TaskCompletionSource<bool> _selectionTcs = new();

    private WPoint _startPoint;
    private Rect _selectionWpfRect;
    private bool _isDragging;
    private bool _processingStarted;

    public Rect Selection { get; private set; }
    public Bitmap? CroppedBitmap { get; private set; }

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public ScreenCaptureWindow(Bitmap screenshot, System.Drawing.Rectangle physBounds)
    {
        _screenshot = screenshot;
        _physBounds = physBounds;
        InitializeComponent();

        Opacity = 0; // prevent OS white-background flash

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        ScreenshotImage.Source = BitmapToDisplaySource(screenshot);

        Closed += (_, _) => _selectionTcs.TrySetResult(false);
    }

    public Task<bool> WaitForSelectionAsync() => _selectionTcs.Task;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        DimPath.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        Opacity = 1;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _dpiX = src.CompositionTarget.TransformToDevice.M11;
            _dpiY = src.CompositionTarget.TransformToDevice.M22;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_inBackgroundMode && msg == WM_NCHITTEST)
        {
            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }
        return IntPtr.Zero;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !_processingStarted)
        {
            _selectionTcs.TrySetResult(false);
            Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_processingStarted) return;
        base.OnMouseLeftButtonDown(e);
        _startPoint  = e.GetPosition(this);
        _isDragging  = true;
        InfoBorder.Visibility    = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        CaptureMouse();
        DrawRect(_startPoint, _startPoint);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_processingStarted) return;
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging) return;
        DrawRect(_startPoint, e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        var rect = Normalize(_startPoint, e.GetPosition(this));
        _selectionWpfRect = rect;
        if (rect.Width < 4 || rect.Height < 4)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            InfoBorder.Visibility = Visibility.Visible;
            return;
        }

        // Convert canvas DIPs → absolute physical screen coords
        double absPhysX = (SystemParameters.VirtualScreenLeft + rect.X) * _dpiX;
        double absPhysY = (SystemParameters.VirtualScreenTop  + rect.Y) * _dpiY;
        int bmpW = Math.Max(1, (int)(rect.Width  * _dpiX));
        int bmpH = Math.Max(1, (int)(rect.Height * _dpiY));

        int bmpX = Math.Clamp((int)(absPhysX - _physBounds.Left), 0, _screenshot.Width  - 1);
        int bmpY = Math.Clamp((int)(absPhysY - _physBounds.Top),  0, _screenshot.Height - 1);
        bmpW = Math.Min(bmpW, _screenshot.Width  - bmpX);
        bmpH = Math.Min(bmpH, _screenshot.Height - bmpY);

        CroppedBitmap = _screenshot.Clone(
            new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
            _screenshot.PixelFormat);
        Selection = new Rect(absPhysX, absPhysY, bmpW, bmpH);

        _processingStarted = true;
        // Keep SelectionRect visible during processing; SwitchToBackgroundMode() will hide it

        ProcessingBorder.Visibility = Visibility.Visible;
        ProcessingBorder.UpdateLayout();
        double cx = rect.X + rect.Width  / 2 - ProcessingBorder.ActualWidth  / 2;
        double cy = rect.Y + rect.Height / 2 - ProcessingBorder.ActualHeight / 2;
        System.Windows.Controls.Canvas.SetLeft(ProcessingBorder, cx);
        System.Windows.Controls.Canvas.SetTop(ProcessingBorder,  cy);

        Cursor = System.Windows.Input.Cursors.Wait;

        _selectionTcs.TrySetResult(true);
    }

    public void ShowProcessingIndicator(bool show)
    {
        ProcessingBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SwitchToBackgroundMode()
    {
        SelectionRect.Visibility    = Visibility.Collapsed;
        ProcessingBorder.Visibility = Visibility.Collapsed;

        var outer = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var inner = new RectangleGeometry(_selectionWpfRect);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(outer);
        group.Children.Add(inner);
        DimPath.Data = group;

        Cursor = null;
        _inBackgroundMode = true;
        _hwndSource?.AddHook(WndProc);
    }

    private void DrawRect(WPoint p1, WPoint p2)
    {
        var r = Normalize(p1, p2);
        System.Windows.Controls.Canvas.SetLeft(SelectionRect, r.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRect,  r.Y);
        SelectionRect.Width  = r.Width;
        SelectionRect.Height = r.Height;
    }

    private static Rect Normalize(WPoint a, WPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private static BitmapSource BitmapToDisplaySource(Bitmap bmp)
    {
        var locked = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(
                bmp.Width, bmp.Height, 96, 96,
                PixelFormats.Bgra32,
                null,
                locked.Scan0,
                Math.Abs(locked.Stride) * bmp.Height,
                locked.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(locked);
        }
    }
}
