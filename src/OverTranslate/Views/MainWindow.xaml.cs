using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using NLog;
using OverTranslate.Services;
using OverTranslate.Views.Capture;
using OverTranslate.Views.Overlay;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Translation;

namespace OverTranslate.Views;

public partial class MainWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _trayMenu;
    private GlobalHotkey? _hotkey;
    private OverlayWindow? _overlayWindow;
    private ScreenCaptureWindow? _captureWindow;
    private ToolbarWindow? _toolbarWindow;
    private GlobalEscapeHook? _escapeHook; // lives for the whole capture session, see CloseAll
    private CancellationTokenSource? _sessionCts; // cancelled on teardown so abandoned work stops
    private EventHandler? _overlayClosedHandler; // tracked so we can detach before re-translate
    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();

    // Kept alive so toolbar translate can re-run OCR/translation on the current selection
    private List<OcrTextBlock> _lastOcrBlocks = [];
    private List<TranslatedBlock> _lastColoredBlocks = [];
    private double _lastSelPhysLeft;
    private double _lastSelPhysTop;
    private double _lastSelPhysWidth;
    private double _lastSelPhysHeight;
    private int _selectionSessionId;

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
        ShowStartupBalloon();
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

    private void ShowStartupBalloon()
    {
        if (_notifyIcon == null) return;

        var hotkeyDisplay = SettingsService.Instance.Current.HotkeyDisplay;
        var shortcutText = string.IsNullOrWhiteSpace(hotkeyDisplay) ? "已設定的快捷鍵" : hotkeyDisplay;

        _notifyIcon.BalloonTipTitle = "OverTranslate 已最小化";
        _notifyIcon.BalloonTipText = $"程式已縮小至系統匣，可使用 {shortcutText} 開始進行截圖翻譯。";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
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
            if (HasActiveSession)
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
            catch (Exception ex)
            {
                Log.Error(ex, "Screen capture failed — aborting selection");
                return;
            }

            // Anything that escapes from here would leave the full-screen dim window on top of the
            // desktop with no owner left to close it, so the whole session setup is guarded.
            try
            {
                Log.Debug("Capture session starting, bounds={Bounds}", screenBounds);
                var captureWindow = new ScreenCaptureWindow(screenshot, screenBounds);
                _captureWindow = captureWindow;
                captureWindow.Show();

                // After Show, not before: everything on the path between creating the window and
                // presenting it delays the first frame, during which the window's black background
                // is what the user sees. The hook is still installed within the same dispatcher
                // pass, so Esc is live long before anyone can press it.
                // Release any previous one first — this hook swallows Esc process-wide, so an
                // orphaned instance would break Esc across the entire desktop, which is far worse
                // than the stuck overlay it exists to prevent.
                DisposeEscapeHook();
                _escapeHook = GlobalEscapeHook.Install(CloseAll);

                CancelSession();
                _sessionCts = new CancellationTokenSource();

                bool selected = await captureWindow.WaitForSelectionAsync();
                if (!selected || !captureWindow.HasSelection)
                {
                    // Also reached when the capture window cancelled itself (its own Esc fallback),
                    // which never goes through CloseAll — so the session is torn down here too.
                    DisposeEscapeHook();
                    CancelSession();
                    captureWindow.Close();
                    _captureWindow = null;
                    screenshot.Dispose();
                    return;
                }

                var settings      = SettingsService.Instance.Current;
                var selection     = captureWindow.Selection;
                EnterOverlayState(captureWindow, selection, [], [], settings.SourceLanguage, hasTranslated: false);

                // Fire in the same pass that built the overlay, before it paints: the toolbar's first
                // frame already reads "翻譯中..." and the overlay's first frame already shows "辨識中".
                // Deferring this to a later dispatcher pass only adds a visible gap where the toolbar
                // sits idle after the selection is done.
                if (settings.AutoTranslateAfterSelection)
                    _toolbarWindow?.RequestTranslate();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Capture session setup failed — tearing down overlay windows");
                CloseAll();
                screenshot.Dispose();
            }
        });
    }

    private void EnterOverlayState(
        ScreenCaptureWindow captureWindow,
        System.Windows.Rect selection,
        List<TranslatedBlock> blocks,
        List<OcrTextBlock> ocrBlocks,
        string srcLang,
        bool hasTranslated)
    {
        _selectionSessionId++;
        _lastOcrBlocks     = ocrBlocks;
        _lastColoredBlocks = blocks;
        _lastSelPhysLeft   = selection.Left;
        _lastSelPhysTop    = selection.Top;
        _lastSelPhysWidth  = selection.Width;
        _lastSelPhysHeight = selection.Height;

        var settings = SettingsService.Instance.Current;
        ShowOverlay(blocks, selection.Left, selection.Top, selection.Width, selection.Height, srcLang, settings.TargetLanguage);

        var toolbar  = new ToolbarWindow(
            selection.Left, selection.Top, selection.Width, selection.Height,
            srcLang, settings.TargetLanguage);
        toolbar.Owner = captureWindow;
        toolbar.TranslateRequested      += OnTranslateRequested;
        toolbar.OpenWindowRequested     += OnOpenWindowRequested;
        toolbar.CopyScreenshotRequested += OnCopyScreenshotRequested;
        toolbar.CloseAllRequested       += (_, _) => CloseAll();
        toolbar.BubblesVisibilityChanged += (_, visible) => _overlayWindow?.SetBubblesVisible(visible);
        _toolbarWindow = toolbar;
        toolbar.SetTranslationState(hasTranslated);
        toolbar.SetToggleEnabled(blocks.Count > 0);
        toolbar.Show();
    }

    // On re-translate: update the existing overlay in-place to avoid z-order fights with
    // ScreenCaptureWindow (both Topmost — close+reopen loses the z-position race).
    // On first call: create a new overlay and wire its Closed handler.
    private void ShowOverlay(
        List<TranslatedBlock> blocks,
        double selPhysLeft,
        double selPhysTop,
        double selPhysWidth,
        double selPhysHeight,
        string sourceLang,
        string targetLang)
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.UpdateBlocks(blocks, selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight, sourceLang, targetLang);
            return;
        }

        _overlayWindow = new OverlayWindow(blocks, selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight, sourceLang, targetLang);
        if (_captureWindow != null)
            _overlayWindow.Owner = _captureWindow;
        // This runs when the overlay closes on its own (Esc via the keyboard hook). Same fault
        // tolerance as CloseAll: a throwing toolbar Close() must not strand the capture window's
        // full-screen dim layer, which is the one window the user cannot get rid of.
        _overlayClosedHandler = (_, _) =>
        {
            _selectionSessionId++;
            DisposeEscapeHook();
            CancelSession();
            ToastWindow.Dismiss();
            CloseWindow(_toolbarWindow, w => w.Close(), nameof(ToolbarWindow));
            _toolbarWindow = null;
            CloseWindow(_captureWindow, w => w.Close(), nameof(ScreenCaptureWindow));
            _captureWindow = null;
            _overlayWindow = null;
        };
        _overlayWindow.Closed += _overlayClosedHandler;
        _overlayWindow.Show();
    }

    private async void OnTranslateRequested(object? sender, TranslateRequest req)
    {
        var requestToolbar = sender as ToolbarWindow;
        var requestCaptureWindow = _captureWindow;
        var requestSessionId = _selectionSessionId;
        // Captured now: _sessionCts is replaced by the next capture, and this request must keep
        // observing the token belonging to the session it was started for.
        var cancellationToken = _sessionCts?.Token ?? CancellationToken.None;
        var settings = SettingsService.Instance.Current;
        var selRect  = requestCaptureWindow?.Selection
            ?? new System.Windows.Rect(_lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowBalloon("缺少 API Key", "請在設定中輸入 API Key。", selRect);
            return;
        }

        requestToolbar?.SetBusy(true);

        try
        {
            if (requestCaptureWindow == null || !requestCaptureWindow.PrepareForTranslation())
            {
                ShowBalloon("辨識失敗", "找不到框選影像，請重新框選。", selRect);
                return;
            }

            _lastSelPhysLeft   = requestCaptureWindow.Selection.Left;
            _lastSelPhysTop    = requestCaptureWindow.Selection.Top;
            _lastSelPhysWidth  = requestCaptureWindow.Selection.Width;
            _lastSelPhysHeight = requestCaptureWindow.Selection.Height;
            selRect = requestCaptureWindow.Selection;

            // The capture window owns CroppedBitmap and disposes it the instant it closes (Esc,
            // CloseAll, re-capture). OCR runs on a thread pool thread and the colour sampling below
            // happens after a second await, so both would read freed GDI+ memory if they used that
            // instance directly. Take our own copy up front — cloning here is safe because we are
            // still on the UI thread with no await since PrepareForTranslation — and let its
            // lifetime match this request instead of the window's.
            using var workBitmap = ClonePixels(requestCaptureWindow.CroppedBitmap!);

            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                "辨識中");

            var recognizedBlocks = await _ocrService.RecognizeAsync(workBitmap, req.SourceLang, cancellationToken);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            _lastOcrBlocks = recognizedBlocks;
            if (_lastOcrBlocks.Count == 0)
            {
                requestToolbar?.SetTranslationState(false);
                ShowBalloon("未偵測到文字", "所選區域中未找到可辨識的文字。", selRect, ToastKind.Info);
                return;
            }

            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                "翻譯中");

            var (translated, _) = await _translationService.TranslateAsync(
                _lastOcrBlocks, req.SourceLang, req.TargetLang, settings.ApiKey,
                cancellationToken: cancellationToken);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            var croppedBitmap = workBitmap;
            var bmpData = croppedBitmap.LockBits(
                new Rectangle(0, 0, croppedBitmap.Width, croppedBitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            // Re-use sampled colors from the previous overlay when available.
            // _lastColoredBlocks may be shorter than translated (e.g. the first
            // translation attempt failed and left _lastColoredBlocks empty), so
            // fall back to defaults rather than throwing IndexOutOfRangeException.
            List<TranslatedBlock> coloredTranslated;
            try
            {
                coloredTranslated = translated
                    .Select((b, i) =>
                    {
                        if (i < _lastColoredBlocks.Count)
                        {
                            return b with
                            {
                                BackgroundColor = _lastColoredBlocks[i].BackgroundColor,
                                TextColor       = _lastColoredBlocks[i].TextColor
                            };
                        }

                        var (bg, fg) = BlockColorSampler.Sample(
                            bmpData, croppedBitmap.Width, croppedBitmap.Height, b.Bounds);
                        return b with { BackgroundColor = bg, TextColor = fg };
                    })
                    .ToList();
            }
            finally
            {
                croppedBitmap.UnlockBits(bmpData);
            }

            _lastColoredBlocks = coloredTranslated;
            ShowOverlay(coloredTranslated, _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight, req.SourceLang, req.TargetLang);
            requestToolbar?.SetTranslationState(true);
            requestToolbar?.SetToggleEnabled(coloredTranslated.Count > 0);
            requestToolbar?.SetEngineBadge(_translationService.LastEngineUsage);
        }
        // The session was torn down (Esc, re-capture, toolbar close) while this was in flight.
        // Expected and user-initiated — it must stay completely silent, with no error toast.
        catch (OperationCanceledException)
        {
            Log.Debug("Translate request abandoned — capture session ended");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("sequence contains no elements", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug(ex, "OCR produced no text blocks");
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            requestToolbar?.SetTranslationState(false);
            ShowBalloon("未偵測到文字", "所選區域中未找到可辨識的文字。", selRect, ToastKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translate request failed (src={Src}, tgt={Tgt}, selection={Sel})",
                req.SourceLang, req.TargetLang, selRect);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            // On failure, restore old bubbles so the overlay isn't left blank
            _overlayWindow?.UpdateBlocks(_lastColoredBlocks, _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight, req.SourceLang, req.TargetLang);
            requestToolbar?.SetTranslationState(_lastColoredBlocks.Count > 0);
            requestToolbar?.SetToggleEnabled(_lastColoredBlocks.Count > 0);
            ShowBalloon("翻譯失敗", $"目前使用的翻譯來源可能暫時無法使用。\n請切換其他翻譯來源後再試一次。\n詳細資訊：{ex.Message}", selRect);
        }
        finally
        {
            if (IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
            {
                _overlayWindow?.RestoreIdle(_lastColoredBlocks.Count > 0);
                requestToolbar?.SetBusy(false);
            }
        }
    }

    // Deep copy of a capture crop, owned by the caller. Clone(Rectangle, PixelFormat) allocates a
    // fresh GDI+ bitmap and copies the pixels, so the copy stays valid after the source is disposed.
    private static Bitmap ClonePixels(Bitmap source) =>
        source.Clone(new Rectangle(0, 0, source.Width, source.Height), source.PixelFormat);

    // Builds the "copy screenshot" image by compositing what the user actually sees in the
    // selection — WITHOUT OverTranslate's own editing chrome. The background is the clean original
    // capture (so the selection border/handles are never included), and the translation bubbles
    // (when present) are rendered on top. The loading indicator is excluded because the bubble
    // layers are empty while processing, so RenderBubblesForSelection returns null then.
    private void OnCopyScreenshotRequested(object? sender, EventArgs e)
    {
        var selRect = new System.Windows.Rect(
            _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);
        try
        {
            var background = _captureWindow?.CreateSelectionImage();
            if (background is null)
            {
                ShowBalloon("複製失敗", "找不到框選影像，請重新框選。", selRect);
                return;
            }

            int w = background.PixelWidth;
            int h = background.PixelHeight;

            var bubbles = _overlayWindow?.RenderBubblesForSelection(
                _lastSelPhysLeft, _lastSelPhysTop, w, h);

            System.Windows.Media.Imaging.BitmapSource result = background;
            if (bubbles is not null)
            {
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var bounds = new System.Windows.Rect(0, 0, w, h);
                    dc.DrawImage(background, bounds);
                    dc.DrawImage(bubbles, bounds);
                }
                var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                composed.Render(dv);
                composed.Freeze();
                result = composed;
            }

            System.Windows.Clipboard.SetImage(result);

            var settings = SettingsService.Instance.Current;
            if (!settings.SaveScreenshotToDisk)
            {
                ShowBalloon("已複製", "已將框選截圖複製到剪貼簿。", selRect, ToastKind.Success);
                return;
            }

            // Saving is a bonus on top of the copy — a failed write must not read as a failed copy.
            try
            {
                var savedPath = ScreenshotSaveService.Save(result, settings.ScreenshotSavePath);
                ShowBalloon("已複製", $"已複製到剪貼簿，並儲存至：\n{savedPath}", selRect, ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Screenshot copied to clipboard but saving to disk failed");
                ShowBalloon("已複製（儲存失敗）", $"已複製到剪貼簿，但無法儲存到本機：{ex.Message}", selRect);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy screenshot failed");
            ShowBalloon("複製失敗", $"無法複製截圖：{ex.Message}", selRect);
        }
    }

    private void OnOpenWindowRequested(object? sender, EventArgs e)
    {
        var srcText = JoinWithoutLineBreaks(_lastOcrBlocks.Select(b => b.Text));
        var tgtText = JoinWithoutLineBreaks(_lastColoredBlocks.Select(b => b.TranslatedText));
        var srcLang = _toolbarWindow?.CurrentSourceLang ?? SettingsService.Instance.Current.SourceLanguage;
        var tgtLang = _toolbarWindow?.CurrentTargetLang ?? SettingsService.Instance.Current.TargetLanguage;

        var shell = ShellWindow.ShowOrActivate(ShellPage.Translation);
        shell.TranslationPage.SetContent(srcText, tgtText, srcLang, tgtLang);

        CloseAll(); // close overlay, dim background, and toolbar
    }

    private static string JoinWithoutLineBreaks(IEnumerable<string> parts) =>
        string.Join(" ", parts.Select(text => text.Replace('\r', ' ').Replace('\n', ' ')));

    private void CloseAll()
    {
        Log.Debug("Tearing down capture session (overlay={Overlay}, toolbar={Toolbar}, capture={Capture})",
            _overlayWindow != null, _toolbarWindow != null, _captureWindow != null);
        _selectionSessionId++;
        DisposeEscapeHook();
        CancelSession();

        // A toast is positioned against the selection it reported on. Once that selection is gone
        // it has nothing left to point at, so it goes with the session rather than lingering on an
        // empty desktop until its own timer runs out.
        ToastWindow.Dismiss();

        // Detach handler before closing so we drive the teardown order ourselves
        if (_overlayWindow != null && _overlayClosedHandler != null)
            _overlayWindow.Closed -= _overlayClosedHandler;

        // Each window is torn down independently: the capture window paints a full-screen dim layer
        // that is click-through once processing starts, so if an earlier Close() threw we must still
        // reach it. Clearing the field first also guarantees the state never claims a window that is
        // actually gone, which would make the next hotkey press a no-op teardown.
        CloseWindow(_overlayWindow, w => w.CloseOverlay(), nameof(OverlayWindow));
        _overlayWindow = null;

        CloseWindow(_toolbarWindow, w => w.Close(), nameof(ToolbarWindow));
        _toolbarWindow = null;

        CloseWindow(_captureWindow, w => w.Close(), nameof(ScreenCaptureWindow));
        _captureWindow = null;
    }

    // The hook is process-wide and swallows Esc, so it must never outlive the session that owns it.
    private void DisposeEscapeHook()
    {
        _escapeHook?.Dispose();
        _escapeHook = null;
    }

    // Signals recognition/translation started by this session to stop. The source is not disposed
    // here: work already in flight still holds the token, and disposing it underneath them would
    // throw. Letting it be collected is the safe trade for an object this small.
    private void CancelSession()
    {
        _sessionCts?.Cancel();
        _sessionCts = null;
    }

    private static void CloseWindow<T>(T? window, Action<T> close, string name) where T : Window
    {
        if (window == null) return;
        try
        {
            close(window);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to close {Window} — forcing teardown of the remaining windows", name);
        }
    }

    // Last-resort teardown for the unhandled-exception handler. Returns whether a capture session
    // was actually torn down, which is what tells the caller the app is back in a clean state.
    internal bool ForceCloseOverlays()
    {
        if (!HasActiveSession) return false;

        CloseAll();
        return true;
    }

    // The Esc hook counts: it is process-wide, so a session that left only the hook behind still
    // needs tearing down even though every window is already gone.
    private bool HasActiveSession =>
        _overlayWindow != null || _toolbarWindow != null || _captureWindow != null || _escapeHook != null;

    private bool IsCurrentSelectionSession(int sessionId, ToolbarWindow? toolbar, ScreenCaptureWindow? captureWindow) =>
        sessionId == _selectionSessionId &&
        ReferenceEquals(toolbar, _toolbarWindow) &&
        ReferenceEquals(captureWindow, _captureWindow);

    private static void OpenSettings() => ShellWindow.ShowOrActivate(ShellPage.Settings);

    private void ShowTrayMenu()
    {
        if (_trayMenu != null) return;
        _trayMenu = new TrayMenuWindow();
        _trayMenu.CaptureRequested         += (_, _) => OnHotkeyPressed(this, EventArgs.Empty);
        _trayMenu.OpenTranslationRequested += (_, _) => OpenTranslationWindow();
        _trayMenu.OpenSettingsRequested    += (_, _) => OpenSettings();
        _trayMenu.ExitRequested            += (_, _) => ExitApp();
        _trayMenu.Closed                   += (_, _) => _trayMenu = null;
        _trayMenu.Show();
    }

    private static void OpenTranslationWindow() =>
        ShellWindow.ShowOrActivate(ShellPage.Translation);

    private void ExitApp()
    {
        DisposeEscapeHook();
        _hotkey?.Dispose();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private static void ShowBalloon(
        string title, string message, System.Windows.Rect? sel = null, ToastKind kind = ToastKind.Error) =>
        ToastWindow.Show(title, message, sel, kind);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}
