using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OverTranslate.Services;

namespace OverTranslate;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _hookId = IntPtr.Zero;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private bool _isLoaded;
    private List<TranslatedBlock> _currentBlocks;
    private double _currentSelectionScreenX;
    private double _currentSelectionScreenY;

    public OverlayWindow(List<TranslatedBlock> blocks, double selectionScreenX, double selectionScreenY)
    {
        InitializeComponent();
        _currentBlocks = blocks;
        _currentSelectionScreenX = selectionScreenX;
        _currentSelectionScreenY = selectionScreenY;

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
            BuildOverlay(_currentBlocks, _currentSelectionScreenX, _currentSelectionScreenY);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);

        InstallKeyboardHook();
    }

    // Shows "翻譯中..." centered on the selection; clears old bubbles so indicator is unobstructed
    public void ShowProcessing(double selPhysX, double selPhysY, double selPhysW, double selPhysH)
    {
        OverlayCanvas.Children.Clear();

        double winPhysLeft = Left * _dpiX;
        double winPhysTop  = Top  * _dpiY;

        ProcessingBorder.Visibility = Visibility.Visible;
        ProcessingBorder.UpdateLayout();
        double cx = (selPhysX + selPhysW / 2 - winPhysLeft) / _dpiX - ProcessingBorder.ActualWidth  / 2;
        double cy = (selPhysY + selPhysH / 2 - winPhysTop)  / _dpiY - ProcessingBorder.ActualHeight / 2;
        Canvas.SetLeft(ProcessingBorder, cx);
        Canvas.SetTop(ProcessingBorder,  cy);
    }

    public void UpdateBlocks(List<TranslatedBlock> blocks, double selScreenX, double selScreenY)
    {
        _currentBlocks = blocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        ProcessingBorder.Visibility = Visibility.Collapsed;
        OverlayCanvas.Visibility    = Visibility.Visible;
        if (_isLoaded)
            BuildOverlay(_currentBlocks, _currentSelectionScreenX, _currentSelectionScreenY);
    }

    public void RestoreIdle(bool hasVisibleBlocks)
    {
        ProcessingBorder.Visibility = Visibility.Collapsed;
        if (hasVisibleBlocks && _isLoaded && OverlayCanvas.Children.Count == 0 && _currentBlocks.Count > 0)
            BuildOverlay(_currentBlocks, _currentSelectionScreenX, _currentSelectionScreenY);
        OverlayCanvas.Visibility = hasVisibleBlocks ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetBubblesVisible(bool visible) =>
        OverlayCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void BuildOverlay(List<TranslatedBlock> blocks, double selScreenX, double selScreenY)
    {
        OverlayCanvas.Children.Clear();

        // Window top-left in physical pixels
        double winPhysLeft = Left * _dpiX;
        double winPhysTop = Top * _dpiY;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            // Physical pixel position on screen
            double physX = selScreenX + block.Bounds.X;
            double physY = selScreenY + block.Bounds.Y;
            double physW = block.Bounds.Width;
            double physH = block.Bounds.Height;

            // Convert to WPF canvas coords (relative to overlay window)
            double canvasX = (physX - winPhysLeft) / _dpiX;
            double canvasY = (physY - winPhysTop) / _dpiY;
            double wpfW = physW / _dpiX;
            double wpfH = physH / _dpiY;

            // Expand coverage 2px beyond OCR bounds on every side to eliminate edge bleed
            const double expand = 2;
            double borderW = Math.Max(wpfW + expand * 2, 30);
            double borderH = wpfH + expand * 2;

            var bg = block.BackgroundColor.A == 0
                ? Colors.White
                : block.BackgroundColor;

            System.Windows.Media.Brush textBrush;
            if (block.TextColor.A != 0)
                textBrush = new SolidColorBrush(block.TextColor);
            else
            {
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                textBrush = lum > 0.5 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            }

            const double minFontSize = 11.0;
            double fontSize = Math.Max(minFontSize, wpfH);
            double innerW = Math.Max(1, borderW - 4);
            var typeface = new Typeface(
                new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var measured = new FormattedText(
                block.TranslatedText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface, fontSize,
                System.Windows.Media.Brushes.Black, _dpiY);

            // 寬度不夠時先嘗試縮小字體；若縮到最小值以下則改用換行
            bool wrap = false;
            if (measured.Width > innerW)
            {
                double scaledFont = fontSize * innerW / measured.Width;
                if (scaledFont >= minFontSize)
                    fontSize = scaledFont;
                else
                {
                    fontSize = minFontSize;
                    wrap = true;
                }
            }

            // 換行時重新量測實際需要的高度，讓區塊往下撐開
            double actualBorderH = borderH;
            if (wrap)
            {
                var wrapMeasured = new FormattedText(
                    block.TranslatedText,
                    CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface, fontSize,
                    System.Windows.Media.Brushes.Black, _dpiY);
                wrapMeasured.MaxTextWidth = innerW;
                actualBorderH = Math.Max(borderH, wrapMeasured.Height + 4);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(bg),
                Padding = new Thickness(2, 1, 2, 1),
                Width  = borderW,
                Height = actualBorderH,
                ClipToBounds = true,
                Child = new TextBlock
                {
                    Text = block.TranslatedText,
                    FontSize = fontSize,
                    Foreground = textBrush,
                    TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                }
            };

            Canvas.SetLeft(border, canvasX - expand);
            Canvas.SetTop(border, canvasY - expand);
            OverlayCanvas.Children.Add(border);
        }
    }

    private void InstallKeyboardHook()
    {
        _keyboardProc = HookCallback;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_ESCAPE)
            {
                Dispatcher.Invoke(CloseOverlay);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void CloseOverlay()
    {
        UninstallKeyboardHook();
        Close();
    }

    private void UninstallKeyboardHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UninstallKeyboardHook();
        base.OnClosed(e);
    }
}
