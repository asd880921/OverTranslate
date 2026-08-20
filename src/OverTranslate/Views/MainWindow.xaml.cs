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
    private GlobalHotkey? _windowHotkey;
    private GlobalHotkey? _realtimePauseHotkey;
    private GlobalHotkey? _quickLookupHotkey;
    private GlobalAuxiliaryHotkeys? _auxiliaryHotkeys;
    private OverlayWindow? _overlayWindow;
    private ScreenCaptureWindow? _captureWindow;
    private ToolbarWindow? _toolbarWindow;
    private GlobalEscapeHook? _escapeHook; // lives for the whole capture session, see CloseAll
    private CancellationTokenSource? _sessionCts; // cancelled on teardown so abandoned work stops
    private EventHandler? _overlayClosedHandler; // tracked so we can detach before re-translate
    // Recognition and translation are not owned here — see AppServices. This window is one of two
    // callers, not the holder, and the call sites below name AppServices directly so that reading
    // any one of them shows where the engine comes from.

    // The voice for the capture toolbar's speak button. Its own instance rather than the
    // translation page's: the two are separate places with separate stop buttons, and one shared
    // player would have the page's speaker silently stop a capture that is still reading.
    private readonly TtsService _tts = new();

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

        // The service reports every end of playback — natural, failed, or stopped — and the toolbar
        // button has to come back from ⏹ to the speaker whichever it was. Start is reflected on the
        // press instead, so the icon does not wait on the fetch.
        _tts.StateChanged += (_, _) => _toolbarWindow?.SetSpeaking(_tts.IsActive);

        InitNotifyIcon();
        RegisterHotkey();
        ShowStartupBalloon();

        // The one thing about a realtime session that has to reach the user outside this
        // application: the window they were watching closed, so the session is over and they are
        // looking at whatever was behind it.
        RealtimeSessionController.Instance.SessionEnded += OnRealtimeSessionEnded;

        // A session composes the monitor without this application's overlays and cannot be told
        // about a window created after it started, so a popup left up would be read back into the
        // subtitles — see OnQuickLookupHotkeyPressed, which is the half that keeps a new one out.
        RealtimeSessionController.Instance.StateChanged += OnRealtimeStateChanged;
    }

    private void OnRealtimeStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (RealtimeSessionController.Instance.IsActive) QuickLookup.QuickLookupWindow.Dismiss();
        }));

    private void OnRealtimeSessionEnded(object? sender, string message) =>
        Dispatcher.Invoke(() => ShowTrayNotification(
            LocalizationService.Get("S.Realtime.SessionEndedTitle"), message));

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

    /// <summary>
    /// Binds the shortcuts the settings say should be live, highest priority first.
    /// </summary>
    /// <remarks>
    /// Through <see cref="HotkeyBindings"/> rather than one <c>Register</c> call per shortcut,
    /// because two of them can want the same combination and Windows resolves that badly: it keys a
    /// registration by window and combination, so the second claim is refused, <c>RegisterHotKey</c>
    /// returns false, and nothing else happens — one shortcut stops working and no code here would
    /// know which. Which one lost would also be whichever happened to be registered second, an
    /// ordering nobody chose.
    ///
    /// The settings page refuses to RECORD a combination another shortcut holds, so this is not
    /// reachable by editing. It is reachable two other ways, and both are the point: settings.json is
    /// a text file someone can edit, and shipping a new shortcut hands every existing installation a
    /// default it never agreed to — anyone already using that combination for something else would
    /// have had one of the two go quiet.
    ///
    /// A shortcut with no action would be absent from the table below rather than registered against
    /// nothing: claiming a combination globally takes it away from every other application, which is
    /// a real cost to pay for a key that would do nothing.
    /// </remarks>
    private void RegisterHotkey()
    {
        var settings = SettingsService.Instance.Current;
        var hwnd = new WindowInteropHelper(this).Handle;

        _hotkey = new GlobalHotkey(GlobalHotkey.CaptureId);
        _hotkey.HotkeyPressed += OnHotkeyPressed;

        // Deliberately quiet, unlike the one above: no startup notification and nothing in the
        // interface naming it. It saves a trip to the tray for a user who already knows it is
        // there, and a user who does not loses nothing by never finding it.
        _windowHotkey = new GlobalHotkey(GlobalHotkey.TranslationWindowId);
        _windowHotkey.HotkeyPressed += OnTranslationWindowHotkeyPressed;

        _realtimePauseHotkey = new GlobalHotkey(GlobalHotkey.RealtimePauseId);
        _realtimePauseHotkey.HotkeyPressed += OnRealtimePauseHotkeyPressed;

        _quickLookupHotkey = new GlobalHotkey(GlobalHotkey.QuickLookupId);
        _quickLookupHotkey.HotkeyPressed += OnQuickLookupHotkeyPressed;

        var hooks = new Dictionary<HotkeyAction, GlobalHotkey>
        {
            [HotkeyAction.Capture] = _hotkey,
            [HotkeyAction.TranslationWindow] = _windowHotkey,
            [HotkeyAction.RealtimePause] = _realtimePauseHotkey,
            [HotkeyAction.QuickLookup] = _quickLookupHotkey,
        };

        var resolved = HotkeyBindings.Resolve(settings);
        foreach (var binding in resolved)
        {
            if (binding.ShadowedBy is { } holder)
            {
                // Warn rather than Debug: the user pressed a key and nothing happened, and this line
                // is the only place that says why.
                Log.Warn(
                    "Hotkey {Action} not registered: {Holder} already claims that trigger",
                    binding.Action, holder);
                continue;
            }

            if (!binding.Enabled)
            {
                Log.Info("Hotkey {Action} is switched off in settings", binding.Action);
                continue;
            }

            // Reachable only through a hand-edited settings.json — the settings page refuses to
            // record it — and refused here for the reason it is refused there: a bare typing key
            // registered globally stops working in every other application.
            if (!HotkeyBindings.IsBindable(binding.Trigger))
            {
                Log.Warn(
                    "Hotkey {Action} not registered: {Display} is a single key that cannot be claimed globally",
                    binding.Action, binding.Trigger.VirtualKey);
                continue;
            }

            if (binding.InputKind == OverTranslate.Models.ShortcutInputKind.Keyboard &&
                hooks.TryGetValue(binding.Action, out var hook))
            {
                hook.Register(hwnd, binding.Modifiers, binding.VirtualKey);
            }
        }

        _auxiliaryHotkeys = new GlobalAuxiliaryHotkeys();
        _auxiliaryHotkeys.ShortcutPressed += OnAuxiliaryHotkeyPressed;
        _auxiliaryHotkeys.Register(resolved);
    }

    private void OnAuxiliaryHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Capture:
                OnHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.TranslationWindow:
                OnTranslationWindowHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.RealtimePause:
                OnRealtimePauseHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.QuickLookup:
                OnQuickLookupHotkeyPressed(this, EventArgs.Empty);
                break;
        }
    }

    private void ShowStartupBalloon()
    {
        var hotkeyDisplay = SettingsService.Instance.Current.HotkeyDisplay;
        var shortcutText = string.IsNullOrWhiteSpace(hotkeyDisplay)
            ? LocalizationService.Get("S.Main.DefaultShortcutName")
            : hotkeyDisplay;

        ShowTrayNotification(
            LocalizationService.Get("S.Main.MinimizedTitle"),
            LocalizationService.Format("S.Main.MinimizedBody", shortcutText));
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

    /// <summary>
    /// Rebinds every shortcut, after any one of them has been changed.
    /// </summary>
    /// <remarks>
    /// All of them rather than the one that changed, because the cost is two Win32 calls and the
    /// alternative is a second entry point that has to be kept in step with which setting the
    /// settings page happened to write.
    /// </remarks>
    public void ReRegisterHotkey()
    {
        _hotkey?.Unregister();
        _windowHotkey?.Unregister();
        _realtimePauseHotkey?.Unregister();
        _quickLookupHotkey?.Unregister();
        _auxiliaryHotkeys?.Dispose();
        _auxiliaryHotkeys = null;
        RegisterHotkey();
    }

    /// <summary>
    /// Starts a capture, unless a realtime session has the screen.
    /// </summary>
    /// <remarks>
    /// This shortcut used to pause and resume a running session, on the reasoning that a session
    /// rules a capture out anyway — so the key was free for the whole of a session that may run for
    /// hours, and pausing is what a user in front of one keeps needing. It was still one key with two
    /// meanings, and the reader could neither choose what to press for the frequent one nor put it
    /// where their hands are while a game has the screen. 暫停 / 繼續 has its own shortcut now — see
    /// <see cref="OnRealtimePauseHotkeyPressed"/> — and this key means the one thing it is named after.
    ///
    /// Which brings back the refusal: the two features share one OCR engine and one bounded pool of
    /// inference slots, so a capture during a session is turned away and told why rather than
    /// competing for them. <see cref="RefuseWhileRealtimeRuns"/> covers block framing too, where a
    /// capture is equally out of the question.
    /// </remarks>
    private void OnHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(async () =>
        {
            if (RefuseWhileRealtimeRuns()) return;

            await RunCaptureSessionAsync();
        });

    /// <summary>
    /// Pauses a running realtime session, or resumes a paused one.
    /// </summary>
    /// <remarks>
    /// Silent when there is no session, and while blocks are being framed: there is nothing running
    /// to pause, and a key named after one action has nothing to say about a mode it does not apply
    /// to. <see cref="Views.Realtime.RealtimeSessionController.TogglePause"/> is what decides that,
    /// and the bar's own button is the other way in.
    /// </remarks>
    private void OnRealtimePauseHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() => Views.Realtime.RealtimeSessionController.Instance.TogglePause());

    /// <summary>
    /// Brings 取詞翻譯's popup up over whatever the user is reading.
    /// </summary>
    /// <remarks>
    /// Not a toggle, unlike the shortcut below it: pressed again it refills the popup with the new
    /// selection rather than dismissing it, because the popup already goes away on its own and the
    /// thing a second press means is "this word now".
    ///
    /// Turned away in the two states where a window of ours must not appear, and for two different
    /// reasons. During a capture the user is framing or reading a screen of their own and a popup
    /// dropped into it takes the foreground away mid-gesture — the same reason the translation
    /// window stays out, and silent for the same reason. During a realtime session it is the session
    /// that cannot afford it: a monitor capture is composed without this application's own overlays
    /// (#94), a popup created afterwards is not on that list, and a session would end up reading and
    /// translating this window's text back to the user. That one is announced, because the shortcut
    /// is otherwise available everywhere and silence would read as breakage.
    /// </remarks>
    private void OnQuickLookupHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(async () =>
        {
            if (HasActiveSession) return;
            if (RefuseWhileRealtimeRuns()) return;

            await Views.QuickLookup.QuickLookupWindow.SummonAsync();
        });

    /// <summary>
    /// Opens the translation window, and during a realtime session brings its layers to the front
    /// instead — the same two answers the tray icon gives, for the same reasons.
    /// </summary>
    /// <remarks>
    /// It never closes the window: every other way in opens or activates, and a shortcut that also
    /// dismissed would be the one thing in the application whose meaning depends on what is already
    /// on screen.
    ///
    /// Two states it does nothing in, for opposite reasons.
    ///
    /// A realtime session is not one of them. The window is no use during a session, but the
    /// shortcut is the fastest way to a control bar that something else has covered, so it does
    /// what the tray icon does and lifts the layers instead.
    ///
    /// A capture in progress is. The selection layer, the overlay and the toolbar are a single
    /// piece of work with its own controls, and dropping a window into the middle of it takes the
    /// foreground away from a screen the user is in the act of framing or reading. The toolbar has
    /// a button for everything reachable from there, so nothing is lost by staying out. Silently,
    /// unlike the capture shortcut's own refusal: that one announces itself because it is the
    /// feature's main entry point and its silence would read as breakage, while this is a
    /// convenience nothing advertises and a notification would be more intrusive than the miss.
    /// </remarks>
    /// <summary>
    /// Brings the shell up, or puts it away when it is already the window in front.
    /// </summary>
    /// <remarks>
    /// A toggle rather than a summons: this is a global shortcut, so it is pressed without looking
    /// for anything to click, and the way back out should be the same key rather than a trip to the
    /// window's own close button.
    ///
    /// Closed, not hidden — the shell is destroyed on close and rebuilt on the next open, which is
    /// what the tray menu's own close already does. IsActive rather than a foreground-window
    /// check: a global hotkey does not move focus, so the window that was in front when the key
    /// went down is still the active one when this runs.
    /// </remarks>
    private void OnTranslationWindowHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            if (HasActiveSession) return;

            if (ShellWindow.Current is { IsActive: true } shell)
            {
                shell.Close();
                return;
            }

            OnTrayLeftClick();
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
    /// Told rather than ignored, because the ways in that still come here are deliberate presses on
    /// a control that looks available — the shell's own button. The capture shortcut no longer goes
    /// through this at all: it has its own answer during a session, see <see cref="OnHotkeyPressed"/>.
    /// </remarks>
    private bool RefuseWhileRealtimeRuns()
    {
        if (!Views.Realtime.RealtimeSessionController.Instance.IsActive) return false;

        ShowTrayNotification(
            LocalizationService.Get("S.Main.RealtimeRunningTitle"),
            LocalizationService.Get("S.Main.RealtimeRunningBody"));
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

            // The box stays adjustable until translation starts, so the toolbar anchored to it has to
            // keep up. Only the toolbar: the overlay carries no bubbles yet at this stage, and the
            // crop is re-read from the window at translate time.
            captureWindow.SelectionAdjusted += (_, selection) =>
            {
                _lastSelPhysLeft   = selection.Left;
                _lastSelPhysTop    = selection.Top;
                _lastSelPhysWidth  = selection.Width;
                _lastSelPhysHeight = selection.Height;
                _toolbarWindow?.FollowSelection(selection);
            };

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
        toolbar.SpeakToggleRequested    += OnSpeakToggleRequested;
        toolbar.SpeakStopRequested      += (_, _) => _tts.Stop();
        _toolbarWindow = toolbar;
        toolbar.SetTranslationState(hasTranslated);
        toolbar.SetToggleEnabled(blocks.Count > 0);
        toolbar.SetSpeakableText(SourceTextForSpeech().Length > 0);
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

        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowBalloon(
                LocalizationService.Get("S.Main.MissingApiKeyTitle"),
                LocalizationService.Get("S.Main.MissingApiKeyBody"), selRect);
            return;
        }

        requestToolbar?.SetBusy(true);

        try
        {
            if (requestCaptureWindow == null || !requestCaptureWindow.PrepareForTranslation())
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.RecogniseFailedTitle"),
                    LocalizationService.Get("S.Main.NoImageBody"), selRect);
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
                LocalizationService.Get("S.Main.Recognising"));

            var recognizedBlocks = await AppServices.Ocr.RecognizeAsync(workBitmap, req.SourceLang, cancellationToken);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            _lastOcrBlocks = recognizedBlocks;
            if (_lastOcrBlocks.Count == 0)
            {
                requestToolbar?.SetTranslationState(false);
                ShowBalloon(
                    LocalizationService.Get("S.Main.NoTextTitle"),
                    LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
                return;
            }

            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                LocalizationService.Get("S.Main.Translating"));

            var (translated, _) = await AppServices.Translation.TranslateAsync(
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
            requestToolbar?.SetEngineBadge(AppServices.Translation.LastEngineUsage);
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
            ShowBalloon(
                LocalizationService.Get("S.Main.NoTextTitle"),
                LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
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
            ShowBalloon(
                LocalizationService.Get("S.Main.TranslateFailedTitle"),
                LocalizationService.Format("S.Main.TranslateFailedBody", ex.Message), selRect);
        }
        finally
        {
            if (IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
            {
                _overlayWindow?.RestoreIdle(_lastColoredBlocks.Count > 0);

                // Here rather than on the success path: recognition is what produces the text to
                // read, and it has run by the time translation fails or finds nothing to translate.
                // A run that failed at the engine still leaves the original on screen and readable.
                requestToolbar?.SetSpeakableText(SourceTextForSpeech().Length > 0);
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
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopyFailedTitle"),
                    LocalizationService.Get("S.Main.NoImageBody"), selRect);
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
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedTitle"),
                    LocalizationService.Get("S.Main.CopiedBody"), selRect, ToastKind.Success);
                return;
            }

            // Saving is a bonus on top of the copy — a failed write must not read as a failed copy.
            try
            {
                var savedPath = ScreenshotSaveService.Save(result, settings.ScreenshotSavePath);
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedTitle"),
                    LocalizationService.Format("S.Main.CopiedAndSavedBody", savedPath), selRect, ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Screenshot copied to clipboard but saving to disk failed");
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedSaveFailedTitle"),
                    LocalizationService.Format("S.Main.CopiedSaveFailedBody", ex.Message), selRect);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy screenshot failed");
            ShowBalloon(
                LocalizationService.Get("S.Main.CopyFailedTitle"),
                LocalizationService.Format("S.Main.CopyFailedBody", ex.Message), selRect);
        }
    }

    /// <summary>
    /// Reads the recognised source text aloud, or stops it if it is already being read.
    /// </summary>
    /// <remarks>
    /// The whole recognised text in one go, joined exactly the way the translation window is opened
    /// with it. The blocks are how the picture was cut up for recognition — one per line of a
    /// subtitle, one per label on a form — not how the sentence was written, so reading them one at
    /// a time would deliver a paragraph as a list of fragments with a pause after each.
    ///
    /// The source text rather than the translation: this is for hearing how the thing on screen is
    /// said, which is also why it needs a real source language and why the button is switched off
    /// until it has one. See <c>ToolbarWindow.RenderSpeakButton</c>.
    /// </remarks>
    private async void OnSpeakToggleRequested(object? sender, EventArgs e)
    {
        if (_tts.IsActive) { _tts.Stop(); return; }
        if (_toolbarWindow is not { } toolbar) return;

        var text = SourceTextForSpeech();
        if (text.Length == 0) return;

        var lang = toolbar.CurrentSourceLang;
        // Belt and braces: the button is already disabled on 自動, and a voice picked for a language
        // nobody chose reads English aloud in Chinese.
        if (Models.LanguageData.IsAutomaticSource(lang)) return;

        // Shown on the press rather than waited for: fetching the audio takes a moment, and the one
        // thing the user needs immediately is the way to stop it.
        toolbar.SetSpeaking(true);
        try
        {
            await _tts.SpeakAsync(text, lang);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Toolbar text-to-speech failed");
            ShowBalloon(
                LocalizationService.Get("S.Main.SpeakFailedTitle"),
                LocalizationService.Format("S.Main.SpeakFailedBody", ex.Message),
                CurrentSelectionRect());
        }
    }

    private string SourceTextForSpeech() =>
        JoinWithoutLineBreaks(_lastOcrBlocks.Select(b => b.Text)).Trim();

    private System.Windows.Rect CurrentSelectionRect() => new(
        _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

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

        // A voice reading a selection that is no longer on screen has nothing left to be about, and
        // nothing would be left to stop it: the button that does is going with the toolbar.
        _tts.Stop();

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
    /// Opens the shell, or — while a realtime session owns the screen — puts its layers back on
    /// top instead.
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

        OpenShell();
    }

    /// <remarks>
    /// No page named: both ways in here — this shortcut and the tray's left click — mean "show me
    /// the window", so it opens on whichever page it was last left on.
    /// </remarks>
    private static void OpenShell() => ShellWindow.ShowOrActivate();

    private void ExitApp()
    {
        // Its overlays are Topmost and click-through; left behind by a shutdown they would be
        // painted onto the desktop with no process left to close them.
        RealtimeSessionController.Instance.Stop();
        DisposeEscapeHook();
        _hotkey?.Dispose();
        _windowHotkey?.Dispose();
        _realtimePauseHotkey?.Dispose();
        _quickLookupHotkey?.Dispose();
        _auxiliaryHotkeys?.Dispose();
        _auxiliaryHotkeys = null;
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
            double lum = OverlayTextColor.PerceivedLuminance(bg);
            return lum > 0.5
                ? System.Windows.Media.Color.FromRgb(0, 0, 0)
                : System.Windows.Media.Color.FromRgb(255, 255, 255);
        }

        var sampled = System.Windows.Media.Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        return OverlayTextColor.Tune(sampled, bg);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}
