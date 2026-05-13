using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
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
    private static readonly Uri CrosshairCursorUri = new("pack://application:,,,/icons/capture_crosshair.cur", UriKind.Absolute);

    private HwndSource? _hwndSource;
    private bool _inBackgroundMode;

    private readonly Bitmap _screenshot;
    private readonly System.Drawing.Rectangle _physBounds;
    private readonly TaskCompletionSource<bool> _selectionTcs = new();

    private WPoint _startPoint;
    private Rect _selectionWpfRect;
    private bool _isDragging;
    private bool _processingStarted;
    private bool _hasSelection;

    public Rect Selection { get; private set; }
    public Bitmap? CroppedBitmap { get; private set; }
    public bool HasSelection => _hasSelection;

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
        Cursor = LoadCrosshairCursor();

        Closed += (_, _) =>
        {
            _selectionTcs.TrySetResult(false);
            CroppedBitmap?.Dispose();
            CroppedBitmap = null;
            _screenshot.Dispose();
        };
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
        if (_processingStarted || _hasSelection) return;
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
            _hasSelection = false;
            SelectionRect.Visibility = Visibility.Collapsed;
            SetHandlesVisibility(false);
            InfoBorder.Visibility = Visibility.Visible;
            return;
        }

        _hasSelection = true;
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();

        _selectionTcs.TrySetResult(true);
    }

    public bool PrepareForTranslation()
    {
        if (!_hasSelection) return false;

        if (_processingStarted)
            return CroppedBitmap != null;

        if (!TryCreateCroppedBitmap())
            return false;

        _processingStarted = true;
        SwitchToBackgroundMode();
        return true;
    }

    public void SwitchToBackgroundMode()
    {
        SelectionRect.Visibility    = Visibility.Collapsed;
        SetHandlesVisibility(false);

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

    private static System.Windows.Input.Cursor LoadCrosshairCursor()
    {
        var streamInfo = System.Windows.Application.GetResourceStream(CrosshairCursorUri);
        if (streamInfo?.Stream == null)
            return System.Windows.Input.Cursors.Cross;

        return new System.Windows.Input.Cursor(streamInfo.Stream);
    }

    private void DrawRect(WPoint p1, WPoint p2)
    {
        var r = Normalize(p1, p2);
        _selectionWpfRect = r;
        System.Windows.Controls.Canvas.SetLeft(SelectionRect, r.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRect,  r.Y);
        SelectionRect.Width  = r.Width;
        SelectionRect.Height = r.Height;
    }

    private void UpdateSelectionMetadata()
    {
        double absPhysX = (SystemParameters.VirtualScreenLeft + _selectionWpfRect.X) * _dpiX;
        double absPhysY = (SystemParameters.VirtualScreenTop  + _selectionWpfRect.Y) * _dpiY;
        int bmpW = Math.Max(1, (int)(_selectionWpfRect.Width  * _dpiX));
        int bmpH = Math.Max(1, (int)(_selectionWpfRect.Height * _dpiY));
        Selection = new Rect(absPhysX, absPhysY, bmpW, bmpH);
    }

    private bool TryCreateCroppedBitmap()
    {
        UpdateSelectionMetadata();

        double absPhysX = Selection.X;
        double absPhysY = Selection.Y;
        int bmpW = Math.Max(1, (int)Selection.Width);
        int bmpH = Math.Max(1, (int)Selection.Height);

        int bmpX = Math.Clamp((int)(absPhysX - _physBounds.Left), 0, _screenshot.Width - 1);
        int bmpY = Math.Clamp((int)(absPhysY - _physBounds.Top),  0, _screenshot.Height - 1);
        bmpW = Math.Min(bmpW, _screenshot.Width  - bmpX);
        bmpH = Math.Min(bmpH, _screenshot.Height - bmpY);
        if (bmpW <= 0 || bmpH <= 0)
            return false;

        CroppedBitmap?.Dispose();
        CroppedBitmap = _screenshot.Clone(
            new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
            _screenshot.PixelFormat);
        Selection = new Rect(absPhysX, absPhysY, bmpW, bmpH);
        return true;
    }

    private void UpdateSelectionVisuals()
    {
        System.Windows.Controls.Canvas.SetLeft(SelectionRect, _selectionWpfRect.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRect,  _selectionWpfRect.Y);
        SelectionRect.Width  = _selectionWpfRect.Width;
        SelectionRect.Height = _selectionWpfRect.Height;
        SelectionRect.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;

        const double halfHandle = 7;
        System.Windows.Controls.Canvas.SetLeft(TopLeftHandle, _selectionWpfRect.Left - halfHandle);
        System.Windows.Controls.Canvas.SetTop(TopLeftHandle, _selectionWpfRect.Top - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(TopRightHandle, _selectionWpfRect.Right - halfHandle);
        System.Windows.Controls.Canvas.SetTop(TopRightHandle, _selectionWpfRect.Top - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(BottomLeftHandle, _selectionWpfRect.Left - halfHandle);
        System.Windows.Controls.Canvas.SetTop(BottomLeftHandle, _selectionWpfRect.Bottom - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(BottomRightHandle, _selectionWpfRect.Right - halfHandle);
        System.Windows.Controls.Canvas.SetTop(BottomRightHandle, _selectionWpfRect.Bottom - halfHandle);

        SetHandlesVisibility(_hasSelection && !_processingStarted);
    }

    private void SetHandlesVisibility(bool visible)
    {
        var handleVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TopLeftHandle.Visibility = handleVisibility;
        TopRightHandle.Visibility = handleVisibility;
        BottomLeftHandle.Visibility = handleVisibility;
        BottomRightHandle.Visibility = handleVisibility;
    }

    private void TopLeftHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Right, _selectionWpfRect.Bottom),
            new WPoint(_selectionWpfRect.Left + e.HorizontalChange, _selectionWpfRect.Top + e.VerticalChange));

    private void TopRightHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Left, _selectionWpfRect.Bottom),
            new WPoint(_selectionWpfRect.Right + e.HorizontalChange, _selectionWpfRect.Top + e.VerticalChange));

    private void BottomLeftHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Right, _selectionWpfRect.Top),
            new WPoint(_selectionWpfRect.Left + e.HorizontalChange, _selectionWpfRect.Bottom + e.VerticalChange));

    private void BottomRightHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Left, _selectionWpfRect.Top),
            new WPoint(_selectionWpfRect.Right + e.HorizontalChange, _selectionWpfRect.Bottom + e.VerticalChange));

    private void ResizeSelectionCorner(WPoint fixedPoint, WPoint movingPoint)
    {
        if (_processingStarted || !_hasSelection) return;

        movingPoint.X = Math.Clamp(movingPoint.X, 0, ActualWidth);
        movingPoint.Y = Math.Clamp(movingPoint.Y, 0, ActualHeight);

        const double minSize = 4;
        if (Math.Abs(movingPoint.X - fixedPoint.X) < minSize)
            movingPoint.X = fixedPoint.X + (movingPoint.X >= fixedPoint.X ? minSize : -minSize);
        if (Math.Abs(movingPoint.Y - fixedPoint.Y) < minSize)
            movingPoint.Y = fixedPoint.Y + (movingPoint.Y >= fixedPoint.Y ? minSize : -minSize);

        movingPoint.X = Math.Clamp(movingPoint.X, 0, ActualWidth);
        movingPoint.Y = Math.Clamp(movingPoint.Y, 0, ActualHeight);

        _selectionWpfRect = Normalize(movingPoint, fixedPoint);
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();
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
