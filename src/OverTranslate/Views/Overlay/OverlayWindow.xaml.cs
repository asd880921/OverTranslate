using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Layout;

namespace OverTranslate.Views.Overlay;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private bool _isLoaded;
    private List<TranslatedBlock> _currentBlocks;
    private double _currentSelectionScreenX;
    private double _currentSelectionScreenY;
    private double _currentSelectionScreenWidth;
    private double _currentSelectionScreenHeight;
    private string _currentSourceLanguage;
    private string _currentTargetLanguage;

    public OverlayWindow(
        List<TranslatedBlock> blocks,
        double selectionScreenX,
        double selectionScreenY,
        double selectionScreenWidth,
        double selectionScreenHeight,
        string sourceLanguage,
        string targetLanguage)
    {
        InitializeComponent();
        _currentBlocks = blocks;
        _currentSelectionScreenX = selectionScreenX;
        _currentSelectionScreenY = selectionScreenY;
        _currentSelectionScreenWidth = selectionScreenWidth;
        _currentSelectionScreenHeight = selectionScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;

        // Cover all screens using WPF DIP coordinates (NOT physical pixel Screen.Bounds).
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) =>
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                _dpiX = src.CompositionTarget.TransformToDevice.M11;
                _dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            _isLoaded = true;
            BuildOverlay();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }

    // Shows a centered status card and clears old bubbles so the indicator is unobstructed.
    public void ShowProcessing(double selPhysX, double selPhysY, double selPhysW, double selPhysH, string statusText)
    {
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();

        double winPhysLeft = Left * _dpiX;
        double winPhysTop  = Top  * _dpiY;

        ProcessingText.Text = statusText;
        ProcessingBorder.Visibility = Visibility.Hidden;
        ProcessingBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ProcessingBorder.DesiredSize;
        double cx = (selPhysX + selPhysW / 2 - winPhysLeft) / _dpiX - desired.Width  / 2;
        double cy = (selPhysY + selPhysH / 2 - winPhysTop)  / _dpiY - desired.Height / 2;
        Canvas.SetLeft(ProcessingBorder, cx);
        Canvas.SetTop(ProcessingBorder,  cy);
        ProcessingBorder.Visibility = Visibility.Visible;
    }

    public void UpdateBlocks(
        List<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight,
        string sourceLanguage,
        string targetLanguage)
    {
        _currentBlocks = blocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        _currentSelectionScreenWidth = selScreenWidth;
        _currentSelectionScreenHeight = selScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        ProcessingBorder.Visibility = Visibility.Collapsed;
        SetTranslationLayersVisible(true);
        if (_isLoaded)
            BuildOverlay();
    }

    public void RestoreIdle(bool hasVisibleBlocks)
    {
        ProcessingBorder.Visibility = Visibility.Collapsed;
        if (hasVisibleBlocks && _isLoaded && BubbleBackgroundCanvas.Children.Count == 0 && _currentBlocks.Count > 0)
            BuildOverlay();
        SetTranslationLayersVisible(hasVisibleBlocks);
    }

    public void SetBubblesVisible(bool visible) => SetTranslationLayersVisible(visible);

    // Renders the translation bubble layers cropped to the given selection region (physical pixels)
    // as a transparent overlay image, for the "copy screenshot" feature. The loading indicator is
    // never included: while processing the bubble canvases are cleared, so the guard below returns
    // null and only the clean original is copied. Returns null when nothing is currently shown
    // (pre-translation, processing, or toggled to original).
    public System.Windows.Media.Imaging.BitmapSource? RenderBubblesForSelection(
        double selPhysLeft, double selPhysTop, int selPhysWidth, int selPhysHeight)
    {
        if (!_isLoaded) return null;
        if (BubbleBackgroundCanvas.Visibility != Visibility.Visible) return null;
        if (BubbleBackgroundCanvas.Children.Count == 0 && BubbleTextCanvas.Children.Count == 0)
            return null;

        int fullW = Math.Max(1, (int)Math.Round(Width  * _dpiX));
        int fullH = Math.Max(1, (int)Math.Round(Height * _dpiY));

        // Render the whole overlay content (both bubble layers) at physical resolution. The
        // processing indicator is Collapsed whenever bubbles exist, so it does not appear.
        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
            fullW, fullH, 96 * _dpiX, 96 * _dpiY, System.Windows.Media.PixelFormats.Pbgra32);
        full.Render((Visual)Content);

        // The overlay window spans the whole virtual screen; the selection sits at this physical
        // offset within it.
        int cropX = Math.Clamp((int)Math.Round(selPhysLeft - Left * _dpiX), 0, fullW - 1);
        int cropY = Math.Clamp((int)Math.Round(selPhysTop  - Top  * _dpiY), 0, fullH - 1);
        int cropW = Math.Clamp(selPhysWidth,  1, fullW - cropX);
        int cropH = Math.Clamp(selPhysHeight, 1, fullH - cropY);

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
            full, new Int32Rect(cropX, cropY, cropW, cropH));
        cropped.Freeze();
        return cropped;
    }

    // Placement and text sizing live in OverlayBubbleLayout, shared with the batch image export so
    // both produce the same result; this only turns the computed bubbles into elements.
    private void BuildOverlay()
    {
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();

        var context = new OverlayLayoutContext(
            DpiX: _dpiX,
            DpiY: _dpiY,
            OriginPhysX: _currentSelectionScreenX,
            OriginPhysY: _currentSelectionScreenY,
            OriginPhysWidth: _currentSelectionScreenWidth,
            OriginPhysHeight: _currentSelectionScreenHeight,
            SurfacePhysLeft: Left * _dpiX,
            SurfacePhysTop: Top * _dpiY,
            CanvasWidth: BubbleBackgroundCanvas.ActualWidth > 0 ? BubbleBackgroundCanvas.ActualWidth : Width,
            CanvasHeight: BubbleBackgroundCanvas.ActualHeight > 0 ? BubbleBackgroundCanvas.ActualHeight : Height,
            SourceLanguage: _currentSourceLanguage,
            TargetLanguage: _currentTargetLanguage);

        OverlayBubbleRenderer.Populate(
            OverlayBubbleLayout.Calculate(_currentBlocks, context),
            BubbleBackgroundCanvas,
            BubbleTextCanvas);
    }

    private void SetTranslationLayersVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BubbleBackgroundCanvas.Visibility = visibility;
        BubbleTextCanvas.Visibility = visibility;
    }

    // Esc is handled by the session-wide GlobalEscapeHook, not here — the overlay only exists
    // once a selection has been drawn, so hosting the hook would leave Esc dead until then.
    public void CloseOverlay() => Close();
}
