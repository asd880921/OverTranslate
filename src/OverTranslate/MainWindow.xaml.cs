using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using OverTranslate.Services;

namespace OverTranslate;

public partial class MainWindow : Window
{
    private NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _trayMenu;
    private GlobalHotkey? _hotkey;
    private OverlayWindow? _overlayWindow;
    private ScreenCaptureWindow? _captureWindow;
    private ToolbarWindow? _toolbarWindow;
    private EventHandler? _overlayClosedHandler; // tracked so we can detach before re-translate
    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();

    // Kept alive so 重新翻譯 can re-translate without re-OCR
    private List<OcrTextBlock> _lastOcrBlocks = [];
    private List<TranslatedBlock> _lastColoredBlocks = [];
    private double _lastSelPhysLeft;
    private double _lastSelPhysTop;
    private double _lastSelPhysWidth;
    private double _lastSelPhysHeight;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void InitializeApp()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        InitNotifyIcon();
        RegisterHotkey();
    }

    private void InitNotifyIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconService.CreateTrayIcon(),
            Text = "OverTranslate",
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)  OpenTranslationWindow();
            if (e.Button == MouseButtons.Right) ShowTrayMenu();
        };
    }

    private void RegisterHotkey()
    {
        var settings = SettingsService.Instance.Current;
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkey = new GlobalHotkey();
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.Register(hwnd, settings.HotkeyModifiers, settings.HotkeyVirtualKey);
    }

    public void ReRegisterHotkey()
    {
        _hotkey?.Unregister();
        RegisterHotkey();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            if (_overlayWindow != null || _toolbarWindow != null)
            {
                CloseAll();
                return;
            }

            Bitmap screenshot;
            System.Drawing.Rectangle screenBounds;
            try
            {
                var allScreens = Screen.AllScreens;
                int left   = allScreens.Min(s => s.Bounds.Left);
                int top    = allScreens.Min(s => s.Bounds.Top);
                int right  = allScreens.Max(s => s.Bounds.Right);
                int bottom = allScreens.Max(s => s.Bounds.Bottom);
                screenBounds = new System.Drawing.Rectangle(left, top, right - left, bottom - top);
                screenshot = new Bitmap(screenBounds.Width, screenBounds.Height);
                using var g = Graphics.FromImage(screenshot);
                g.CopyFromScreen(left, top, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
            }
            catch { return; }

            var captureWindow = new ScreenCaptureWindow(screenshot, screenBounds);
            captureWindow.Show();

            bool selected = await captureWindow.WaitForSelectionAsync();
            if (!selected || captureWindow.CroppedBitmap == null)
            {
                captureWindow.Close();
                screenshot.Dispose();
                return;
            }

            var settings      = SettingsService.Instance.Current;
            var selection     = captureWindow.Selection;
            var croppedBitmap = captureWindow.CroppedBitmap;

            // OCR
            List<OcrTextBlock> ocrBlocks;
            try
            {
                ocrBlocks = await _ocrService.RecognizeAsync(croppedBitmap, settings.SourceLanguage);
            }
            catch (Exception ex)
            {
                croppedBitmap.Dispose(); screenshot.Dispose();
                EnterOverlayState(captureWindow, selection, [], [], settings.SourceLanguage);
                ShowBalloon("辨識失敗", ex.Message, selection);
                return;
            }

            if (ocrBlocks.Count == 0)
            {
                croppedBitmap.Dispose(); screenshot.Dispose();
                EnterOverlayState(captureWindow, selection, [], [], settings.SourceLanguage);
                ShowBalloon("未偵測到文字", "所選區域中未找到可辨識的文字。", selection);
                return;
            }

            // API key check
            if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                croppedBitmap.Dispose(); screenshot.Dispose();
                EnterOverlayState(captureWindow, selection, [], ocrBlocks, settings.SourceLanguage);
                ShowBalloon("缺少 API Key", "請在設定中輸入 API Key。", selection);
                return;
            }

            // Translate
            List<TranslatedBlock> translated;
            string detectedLang;
            try
            {
                (translated, detectedLang) = await _translationService.TranslateAsync(
                    ocrBlocks, settings.SourceLanguage, settings.TargetLanguage, settings.ApiKey);
            }
            catch (Exception ex)
            {
                croppedBitmap.Dispose(); screenshot.Dispose();
                EnterOverlayState(captureWindow, selection, [], ocrBlocks, settings.SourceLanguage);
                ShowBalloon("翻譯失敗", ex.Message, selection);
                return;
            }

            // Sample background and text colors
            var bmpData = croppedBitmap.LockBits(
                new Rectangle(0, 0, croppedBitmap.Width, croppedBitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var coloredTranslated = translated.Select(b =>
            {
                var bg = SampleAverageColor(bmpData, croppedBitmap.Width, croppedBitmap.Height, b.Bounds);
                var fg = SampleTextColor(bmpData, croppedBitmap.Width, croppedBitmap.Height, b.Bounds, bg);
                return b with { BackgroundColor = bg, TextColor = fg };
            }).ToList();
            croppedBitmap.UnlockBits(bmpData);
            croppedBitmap.Dispose();
            screenshot.Dispose();

            var srcLangForToolbar = settings.SourceLanguage == "auto" && !string.IsNullOrEmpty(detectedLang)
                ? detectedLang
                : settings.SourceLanguage;

            EnterOverlayState(captureWindow, selection, coloredTranslated, ocrBlocks, srcLangForToolbar);
        });
    }

    private void EnterOverlayState(
        ScreenCaptureWindow captureWindow,
        System.Windows.Rect selection,
        List<TranslatedBlock> blocks,
        List<OcrTextBlock> ocrBlocks,
        string srcLang)
    {
        _lastOcrBlocks     = ocrBlocks;
        _lastColoredBlocks = blocks;
        _lastSelPhysLeft   = selection.Left;
        _lastSelPhysTop    = selection.Top;
        _lastSelPhysWidth  = selection.Width;
        _lastSelPhysHeight = selection.Height;

        captureWindow.SwitchToBackgroundMode();
        _captureWindow = captureWindow;

        ShowOverlay(blocks, selection.Left, selection.Top);

        var settings = SettingsService.Instance.Current;
        var toolbar  = new ToolbarWindow(
            selection.Left, selection.Top, selection.Width, selection.Height,
            srcLang, settings.TargetLanguage);
        toolbar.RetranslateRequested    += OnRetranslateRequested;
        toolbar.OpenWindowRequested     += OnOpenWindowRequested;
        toolbar.CloseAllRequested       += (_, _) => CloseAll();
        toolbar.BubblesVisibilityChanged += (_, visible) => _overlayWindow?.SetBubblesVisible(visible);
        _toolbarWindow = toolbar;
        toolbar.SetToggleEnabled(blocks.Count > 0);
        toolbar.Show();
    }

    // On re-translate: update the existing overlay in-place to avoid z-order fights with
    // ScreenCaptureWindow (both Topmost — close+reopen loses the z-position race).
    // On first call: create a new overlay and wire its Closed handler.
    private void ShowOverlay(List<TranslatedBlock> blocks, double selPhysLeft, double selPhysTop)
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.UpdateBlocks(blocks, selPhysLeft, selPhysTop);
            return;
        }

        _overlayWindow = new OverlayWindow(blocks, selPhysLeft, selPhysTop);
        _overlayClosedHandler = (_, _) =>
        {
            _toolbarWindow?.Close();
            _toolbarWindow = null;
            _captureWindow?.Close();
            _captureWindow = null;
            _overlayWindow = null;
        };
        _overlayWindow.Closed += _overlayClosedHandler;
        _overlayWindow.Show();
    }

    private async void OnRetranslateRequested(object? sender, RetranslateRequest req)
    {
        if (_lastOcrBlocks.Count == 0) return;

        var settings = SettingsService.Instance.Current;
        var selRect  = new System.Windows.Rect(_lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowBalloon("缺少 API Key", "請在設定中輸入 API Key。", selRect);
            return;
        }

        _toolbarWindow?.SetBusy(true);
        // Show indicator inside OverlayWindow so it's above the translation bubbles
        _overlayWindow?.ShowProcessing(_lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

        try
        {
            var (translated, _) = await _translationService.TranslateAsync(
                _lastOcrBlocks, req.SourceLang, req.TargetLang, settings.ApiKey);

            // Re-use sampled colors from the previous overlay when available.
            // _lastColoredBlocks may be shorter than translated (e.g. the first
            // translation attempt failed and left _lastColoredBlocks empty), so
            // fall back to defaults rather than throwing IndexOutOfRangeException.
            var coloredTranslated = translated
                .Select((b, i) => i < _lastColoredBlocks.Count
                    ? b with {
                        BackgroundColor = _lastColoredBlocks[i].BackgroundColor,
                        TextColor       = _lastColoredBlocks[i].TextColor
                      }
                    : b)
                .ToList();

            _lastColoredBlocks = coloredTranslated;
            ShowOverlay(coloredTranslated, _lastSelPhysLeft, _lastSelPhysTop);
            _toolbarWindow?.SetToggleEnabled(coloredTranslated.Count > 0);
        }
        catch (Exception ex)
        {
            // On failure, restore old bubbles so the overlay isn't left blank
            _overlayWindow?.UpdateBlocks(_lastColoredBlocks, _lastSelPhysLeft, _lastSelPhysTop);
            _toolbarWindow?.SetToggleEnabled(_lastColoredBlocks.Count > 0);
            ShowBalloon("翻譯失敗", ex.Message, selRect);
        }
        finally
        {
            _toolbarWindow?.SetBusy(false);
        }
    }

    private void OnOpenWindowRequested(object? sender, EventArgs e)
    {
        var srcText = string.Join("\n", _lastOcrBlocks.Select(b => b.Text));
        var tgtText = string.Join("\n", _lastColoredBlocks.Select(b => b.TranslatedText));
        var srcLang = _toolbarWindow?.CurrentSourceLang ?? SettingsService.Instance.Current.SourceLanguage;
        var tgtLang = _toolbarWindow?.CurrentTargetLang ?? SettingsService.Instance.Current.TargetLanguage;

        var win = new TranslationWindow(srcText, tgtText, srcLang, tgtLang);
        win.Show();
        win.Activate();

        CloseAll(); // close overlay, dim background, and toolbar
    }

    private void CloseAll()
    {
        // Detach handler before closing so we drive the teardown order ourselves
        if (_overlayWindow != null && _overlayClosedHandler != null)
            _overlayWindow.Closed -= _overlayClosedHandler;
        _overlayWindow?.CloseOverlay();
        _overlayWindow = null;

        _toolbarWindow?.Close();
        _toolbarWindow = null;

        _captureWindow?.Close();
        _captureWindow = null;
    }

    private static void OpenSettings() => SettingsWindow.ShowOrActivate();

    private void ShowTrayMenu()
    {
        if (_trayMenu != null) return;
        _trayMenu = new TrayMenuWindow();
        _trayMenu.OpenTranslationRequested += (_, _) => OpenTranslationWindow();
        _trayMenu.OpenSettingsRequested    += (_, _) => OpenSettings();
        _trayMenu.OpenAboutRequested       += (_, _) => AboutWindow.ShowOrActivate();
        _trayMenu.ExitRequested            += (_, _) => ExitApp();
        _trayMenu.Closed                   += (_, _) => _trayMenu = null;
        _trayMenu.Show();
    }

    private static void OpenTranslationWindow() =>
        ((App)System.Windows.Application.Current).ShowOrActivateTranslationWindow();

    private void ExitApp()
    {
        _hotkey?.Dispose();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private static void ShowBalloon(string title, string message, System.Windows.Rect? sel = null)
    {
        new ToastWindow(title, message, sel).Show();
    }

    private static System.Windows.Media.Color SampleAverageColor(
        BitmapData data, int bmpW, int bmpH, System.Windows.Rect bounds)
    {
        int x1 = Math.Clamp((int)bounds.X, 0, bmpW);
        int y1 = Math.Clamp((int)bounds.Y, 0, bmpH);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width),  0, bmpW);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bmpH);

        int stripH = Math.Max(3, (y2 - y1) / 3);
        int abY1 = Math.Max(0,   y1 - stripH);
        int abY2 = y1;
        int blY1 = y2;
        int blY2 = Math.Min(bmpH, y2 + stripH);

        long r = 0, g = 0, b = 0, n = 0;

        void Scan(int sy1, int sy2)
        {
            for (int py = sy1; py < sy2; py++)
                for (int px = x1; px < x2; px += 2)
                {
                    int v = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                    b += v & 0xFF;
                    g += (v >> 8)  & 0xFF;
                    r += (v >> 16) & 0xFF;
                    n++;
                }
        }

        if (abY2 > abY1) Scan(abY1, abY2);
        if (blY2 > blY1) Scan(blY1, blY2);
        if (n == 0) Scan(y1, y2);

        return n == 0
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static System.Windows.Media.Color SampleTextColor(
        BitmapData data, int bmpW, int bmpH, System.Windows.Rect bounds,
        System.Windows.Media.Color bg)
    {
        int x1 = Math.Clamp((int)bounds.X, 0, bmpW);
        int y1 = Math.Clamp((int)bounds.Y, 0, bmpH);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width),  0, bmpW);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bmpH);

        long r = 0, g = 0, b = 0, n = 0;
        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px += 2)
            {
                int v  = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                byte vB = (byte)(v & 0xFF);
                byte vG = (byte)((v >> 8) & 0xFF);
                byte vR = (byte)((v >> 16) & 0xFF);
                int diff = Math.Abs(vR - bg.R) + Math.Abs(vG - bg.G) + Math.Abs(vB - bg.B);
                if (diff > 60) { r += vR; g += vG; b += vB; n++; }
            }

        if (n == 0)
        {
            double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return lum > 0.5
                ? System.Windows.Media.Color.FromRgb(0, 0, 0)
                : System.Windows.Media.Color.FromRgb(255, 255, 255);
        }
        return System.Windows.Media.Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}
