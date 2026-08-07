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
using OverTranslate.Views.Realtime;
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

    // Lent to the realtime translation session, which runs its own loop but must not load a second
    // ONNX runtime or open a second set of HTTP handles to do it. Sharing also means the two
    // features queue on one inference gate instead of splitting the CPU between them.
    internal OcrService SharedOcrService => _ocrService;
    internal TranslationService SharedTranslationService => _translationService;

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
            if (e.Button == MouseButtons.Left)  OnTrayLeftClick();
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
        var hotkeyDisplay = SettingsService.Instance.Current.HotkeyDisplay;
        var shortcutText = string.IsNullOrWhiteSpace(hotkeyDisplay) ? "已設定的快捷鍵" : hotkeyDisplay;

        ShowTrayNotification(
            "OverTranslate 已最小化",
            $"程式已縮小至系統匣，可使用 {shortcutText} 開始進行截圖翻譯。");
    }

    /// <summary>
    /// A notification through the tray icon, which Windows presents in its own notification centre.
    /// </summary>
    /// <remarks>
    /// The application's own <see cref="ToastWindow"/> is for things that belong to a capture — it
    /// appears beside the selection it is talking about and disappears with it. Something the user
    /// caused from outside any capture, such as a shortcut that declined to start one, has no such
    /// anchor, and telling them through the shell they already associate with this application is
    /// both less startling and something they can go back and read.
    /// </remarks>
    private void ShowTrayNotification(string title, string message)
    {
        if (_notifyIcon == null) return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void ReRegisterHotkey()
    {
        _hotkey?.Unregister();
        RegisterHotkey();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(async () =>
        {
            if (RefuseWhileRealtimeRuns()) return;
            await RunCaptureSessionAsync();
        });

    /// <summary>
    /// Turns a capture away while a realtime session owns the screen, and says why.
    /// </summary>
    /// <remarks>
    /// The two features share one OCR engine and one bounded pool of inference slots, and a
    /// realtime session uses them continuously. Running a capture alongside it would have them
    /// competing for those slots, and — if the two were set to different source languages — swapping
    /// the loaded model back and forth between every read. See OcrEngineConcurrencyTests for what
    /// that measured out as before this rule existed.
    ///
    /// Told rather than ignored: the shortcut worked a moment ago, so silence would read as the
    /// application having broken rather than as a deliberate rule.
    /// </remarks>
    private bool RefuseWhileRealtimeRuns()
    {
        if (!Views.Realtime.RealtimeSessionController.Instance.IsActive) return false;

        ShowTrayNotification("即時翻譯進行中", "請先結束即時翻譯，再使用截圖翻譯。");
        return true;
    }

    /// <summary>
    /// Starts a capture from the shell window's nav rail. The shell has to leave the screen first:
    /// <see cref="RunCaptureSessionAsync"/> grabs the desktop with a synchronous CopyFromScreen, so
    /// a still-visible shell ends up baked into the very image the user is about to select from.
    /// <see cref="WindowScreenPresence.HideAndWaitForScreen"/> is what makes that ordering real —
    /// it does not return until the compositor has presented a frame without the window.
    /// </summary>
    public void StartCaptureFromShell(Window shell)
    {
        // The rail's button is disabled while a session runs, so this is the guard rather than the
        // notice — but it is the one that actually enforces the rule, and a disabled button is a
        // presentation detail that a future layout change could drop.
        if (RefuseWhileRealtimeRuns()) return;

        if (HasActiveSession)
        {
            CloseAll();
            return;
        }

        _shellHiddenForCapture = shell;
        WindowScreenPresence.HideAndWaitForScreen(shell);

        // Started inline, not queued. This used to go through the dispatcher at Background
        // priority, which sits *below* Input: hiding the shell hands activation to another window
        // and the user is already moving the mouse toward what they want to select, so the queued
        // capture kept being overtaken by that input and started whenever the stream happened to
        // pause. There is nothing left to wait for either — HideAndWaitForScreen has already
        // cleared the screen, and the button whose press feedback the deferral used to protect is
        // no longer visible.
        _ = RunShellCaptureAsync();
    }

    private async Task RunShellCaptureAsync()
    {
        try
        {
            await RunCaptureSessionAsync("shell-button");
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception would otherwise be silent.
            Log.Error(ex, "Capture started from the shell window failed");
        }
        finally
        {
            // A live session owns the screen, and the shell stays away until it ends — CloseAll and
            // the overlay's own teardown both restore it. No session here means the user cancelled
            // during selection, and a window that vanished because they pressed a button inside it
            // must come straight back.
            if (!HasActiveSession)
                RestoreShellAfterCapture();
        }
    }

    // Set for the lifetime of a shell-initiated capture. Null for hotkey captures, which never hid
    // anything and so have nothing to put back.
    private Window? _shellHiddenForCapture;

    private void RestoreShellAfterCapture()
    {
        var shell = _shellHiddenForCapture;
        _shellHiddenForCapture = null;
        // Already visible when the toolbar's 開啟翻譯視窗 carried the result into it, which shows
        // the shell itself before tearing the session down.
        if (shell is null || shell.IsVisible) return;

        try
        {
            shell.Show();
            shell.Activate();
        }
        catch (Exception ex)
        {
            // Racing an app shutdown that already destroyed the window. Nothing left to restore,
            // and it must not take the session teardown down with it.
            Log.Warn(ex, "Could not restore the shell window after a capture");
        }
    }

    private async Task RunCaptureSessionAsync(string origin = "hotkey")
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
            screenBounds = ScreenGeometry.VirtualDesktopBounds();
            screenshot = new Bitmap(screenBounds.Width, screenBounds.Height);
            using var g = Graphics.FromImage(screenshot);
            g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0,
                screenBounds.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screen capture failed — aborting selection");
            return;
        }

        // After the capture, never before: with the level enabled this queries every monitor and
        // writes a multi-line record, which on the way in would delay the freeze the user just
        // asked for. The values it reports are the same either side of the capture.
        DisplayDiagnostics.LogSnapshot(origin);

        // Anything that escapes from here would leave the full-screen dim window on top of the
        // desktop with no owner left to close it, so the whole session setup is guarded.
        try
        {
            Log.Info("Capture session starting, origin={Origin}, bounds={Bounds}", origin, screenBounds);
            var captureWindow = new ScreenCaptureWindow(screenshot, screenBounds);
            _captureWindow = captureWindow;
            captureWindow.Show();

            // Diagnostic: where the dim window physically landed and at what scale, versus the
            // screenBounds the screenshot was taken with. A difference between the two is the
            // misalignment users report, and Stretch="Fill" makes it invisible otherwise.
            DisplayDiagnostics.LogSnapshot("capture-window-shown", captureWindow);

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
            RestoreShellAfterCapture();
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

                        var bg = SampleAverageColor(
                            bmpData,
                            croppedBitmap.Width,
                            croppedBitmap.Height,
                            b.Bounds,
                            req.SourceLang);
                        var fg = SampleTextColor(bmpData, croppedBitmap.Width, croppedBitmap.Height, b.Bounds, bg);
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
        // Paired with "Capture session starting": between them they show whether a session the user
        // reports as stuck ever actually ended.
        Log.Info("Tearing down capture session (overlay={Overlay}, toolbar={Toolbar}, capture={Capture})",
            _overlayWindow != null, _toolbarWindow != null, _captureWindow != null);
        _selectionSessionId++;
        DisposeEscapeHook();
        CancelSession();

        // A toast is positioned against the selection it reported on. Once that selection is gone it
        // has nothing left to point at, so it goes with the session rather than lingering on an
        // empty desktop until its own timer runs out. The close button on the toast is what covers
        // the reader who wants it gone sooner.
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

        // Last, so a shell hidden for this capture comes back only once the screen is clear of the
        // dim layer and overlay it would otherwise be raised behind.
        RestoreShellAfterCapture();
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

    /// <summary>
    /// Whether a screenshot capture — selection, overlay or toolbar — is on screen right now, so
    /// the other feature can decline to start on top of it.
    /// </summary>
    public bool IsCapturing => HasActiveSession;

    private bool IsCurrentSelectionSession(int sessionId, ToolbarWindow? toolbar, ScreenCaptureWindow? captureWindow) =>
        sessionId == _selectionSessionId &&
        ReferenceEquals(toolbar, _toolbarWindow) &&
        ReferenceEquals(captureWindow, _captureWindow);

    private static void OpenSettings() => ShellWindow.ShowOrActivate(ShellPage.Settings);

    private void ShowTrayMenu()
    {
        if (_trayMenu != null) return;
        _trayMenu = new TrayMenuWindow();
        _trayMenu.OpenTranslationRequested += (_, _) => OnTrayLeftClick();
        _trayMenu.SetRealtimeRunning(Views.Realtime.RealtimeSessionController.Instance.IsActive);
        _trayMenu.OpenSettingsRequested    += (_, _) => OpenSettings();
        _trayMenu.ExitRequested            += (_, _) => ExitApp();
        _trayMenu.Closed                   += (_, _) => _trayMenu = null;
        _trayMenu.Show();
    }

    /// <summary>
    /// Opens the translation window, or — while a realtime session owns the screen — puts its
    /// layers back on top instead.
    /// </summary>
    /// <remarks>
    /// The window is no use during a session: the layers cover the screen and the session's own
    /// controls are the only thing to interact with. So the click is spent on the one thing the
    /// user might actually need it for, which is reaching a control bar that something else has
    /// covered. Without it the only way out of that would be killing the application from the
    /// tray, which takes the block layout with it.
    /// </remarks>
    private static void OnTrayLeftClick()
    {
        if (Views.Realtime.RealtimeSessionController.Instance.IsActive)
        {
            Views.Realtime.RealtimeSessionController.Instance.BringToFront();
            return;
        }

        OpenTranslationWindow();
    }

    private static void OpenTranslationWindow() =>
        ShellWindow.ShowOrActivate(ShellPage.Translation);

    private void ExitApp()
    {
        // Its overlays are Topmost and click-through; left behind by a shutdown they would be
        // painted onto the desktop with no process left to close them.
        RealtimeSessionController.Instance.Stop();
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

    private static System.Windows.Media.Color SampleAverageColor(
        BitmapData data, int bmpW, int bmpH, System.Windows.Rect bounds, string sourceLanguage)
    {
        // All scripts use the outer dominant-color sampler. It pads outward from the text box
        // and picks the most common surrounding color, so it stays correct even when the
        // (tightened) box no longer fully encloses the glyphs. The earlier English-only
        // strip-average sampled thin bands directly above/below the box; once the box height
        // was reduced those bands grazed the light glyphs and produced a washed-out grey that
        // no longer blended with the dark page background.
        _ = sourceLanguage;
        return SampleOuterDominantBackgroundColor(data, bmpW, bmpH, bounds);
    }

    private static System.Windows.Media.Color SampleOuterDominantBackgroundColor(
        BitmapData data, int bmpW, int bmpH, System.Windows.Rect bounds)
    {
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        int x1 = Math.Clamp((int)bounds.X - padX, 0, bmpW);
        int y1 = Math.Clamp((int)bounds.Y - padY, 0, bmpH);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width) + padX, 0, bmpW);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height) + padY, 0, bmpH);
        int innerX1 = Math.Clamp((int)bounds.X, 0, bmpW);
        int innerY1 = Math.Clamp((int)bounds.Y, 0, bmpH);
        int innerX2 = Math.Clamp((int)(bounds.X + bounds.Width), 0, bmpW);
        int innerY2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bmpH);

        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();

        void AddPixel(int px, int py)
        {
            int v = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
            byte b = (byte)(v & 0xFF);
            byte g = (byte)((v >> 8) & 0xFF);
            byte r = (byte)((v >> 16) & 0xFF);
            int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            var bucket = buckets.GetValueOrDefault(key);
            buckets[key] = (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1);
        }

        for (int py = y1; py < y2; py++)
        {
            for (int px = x1; px < x2; px += 2)
            {
                bool insideTextRect = px >= innerX1 && px < innerX2 && py >= innerY1 && py < innerY2;
                if (!insideTextRect)
                    AddPixel(px, py);
            }
        }

        if (buckets.Count == 0)
            return System.Windows.Media.Colors.White;

        var dominant = buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .First();

        return System.Windows.Media.Color.FromRgb(
            (byte)(dominant.R / dominant.Count),
            (byte)(dominant.G / dominant.Count),
            (byte)(dominant.B / dominant.Count));
    }

    private static System.Windows.Media.Color SampleTextColor(
        BitmapData data, int bmpW, int bmpH, System.Windows.Rect bounds,
        System.Windows.Media.Color bg)
    {
        int x1 = Math.Clamp((int)bounds.X, 0, bmpW);
        int y1 = Math.Clamp((int)bounds.Y, 0, bmpH);
        int x2 = Math.Clamp((int)(bounds.X + bounds.Width),  0, bmpW);
        int y2 = Math.Clamp((int)(bounds.Y + bounds.Height), 0, bmpH);

        int maxDiff = 0;
        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px += 2)
            {
                int v  = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                byte vB = (byte)(v & 0xFF);
                byte vG = (byte)((v >> 8) & 0xFF);
                byte vR = (byte)((v >> 16) & 0xFF);
                int diff = Math.Abs(vR - bg.R) + Math.Abs(vG - bg.G) + Math.Abs(vB - bg.B);
                if (diff > maxDiff)
                    maxDiff = diff;
            }

        int diffThreshold = Math.Max(60, (int)(maxDiff * 0.6));
        long r = 0, g = 0, b = 0, n = 0;
        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px += 2)
            {
                int v  = Marshal.ReadInt32(data.Scan0, py * data.Stride + px * 4);
                byte vB = (byte)(v & 0xFF);
                byte vG = (byte)((v >> 8) & 0xFF);
                byte vR = (byte)((v >> 16) & 0xFF);
                int diff = Math.Abs(vR - bg.R) + Math.Abs(vG - bg.G) + Math.Abs(vB - bg.B);
                if (diff >= diffThreshold) { r += vR; g += vG; b += vB; n++; }
            }

        if (n == 0)
        {
            double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return lum > 0.5
                ? System.Windows.Media.Color.FromRgb(0, 0, 0)
                : System.Windows.Media.Color.FromRgb(255, 255, 255);
        }

        var sampled = System.Windows.Media.Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        return TuneOverlayTextColor(sampled, bg);
    }

    private static System.Windows.Media.Color TuneOverlayTextColor(
        System.Windows.Media.Color text,
        System.Windows.Media.Color background)
    {
        double bgLum = GetPerceivedLuminance(background);
        double textLum = GetPerceivedLuminance(text);
        var (h, s, l) = RgbToHsl(text);
        bool isNearNeutral = s < 0.18;
        bool isNearBlack = textLum < 0.16;

        if (isNearNeutral)
        {
            if (isNearBlack)
                return text;

            if (bgLum >= 0.55)
            {
                double maxAllowedLum = Math.Max(0.08, bgLum - 0.24);
                double boostedLum = Math.Min(maxAllowedLum, textLum + 0.12);
                double boostedLightness = Math.Min(0.74, l + 0.1);
                return HslToRgb(h, Math.Min(0.22, s * 1.08), Math.Max(boostedLightness, boostedLum));
            }

            double minAllowedLum = Math.Min(0.92, bgLum + 0.3);
            double liftedLum = Math.Max(minAllowedLum, textLum + 0.08);
            double liftedLightness = Math.Max(l, Math.Min(0.9, l + 0.08));
            return HslToRgb(h, Math.Min(0.22, s * 1.08), Math.Max(liftedLightness, liftedLum));
        }

        // For colored text, preserve hue and only nudge brightness slightly.
        // The main correction is stronger saturation so sampled colors feel closer
        // to the source instead of getting washed out by antialiasing.
        double targetSaturation = Math.Min(1.0, Math.Max(s + 0.08, s * 1.12));

        if (bgLum >= 0.55)
        {
            double maxAllowedLum = Math.Max(0.08, bgLum - 0.24);
            double adjustedLum = Math.Min(maxAllowedLum, textLum + 0.01);
            double adjustedLightness = Math.Min(0.64, Math.Max(l, l + 0.01));
            return HslToRgb(h, targetSaturation, Math.Max(adjustedLightness, adjustedLum));
        }

        double minAllowedColorLum = Math.Min(0.9, bgLum + 0.22);
        double colorLum = Math.Max(minAllowedColorLum, textLum + 0.01);
        double colorLightness = Math.Max(l, Math.Min(0.8, l + 0.01));
        return HslToRgb(h, targetSaturation, Math.Max(colorLightness, colorLum));
    }

    private static double GetPerceivedLuminance(System.Windows.Media.Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

    private static (double H, double S, double L) RgbToHsl(System.Windows.Media.Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0;
        double l = (max + min) / 2.0;

        if (Math.Abs(max - min) < double.Epsilon)
            return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        if (Math.Abs(max - r) < double.Epsilon)
            h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
        else if (Math.Abs(max - g) < double.Epsilon)
            h = ((b - r) / d + 2) / 6.0;
        else
            h = ((r - g) / d + 4) / 6.0;

        return (h, s, l);
    }

    private static System.Windows.Media.Color HslToRgb(double h, double s, double l)
    {
        h = h - Math.Floor(h);
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 0)
        {
            byte gray = (byte)Math.Round(l * 255);
            return System.Windows.Media.Color.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}
