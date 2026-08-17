using System.Windows;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Views.Realtime;

/// <param name="ScreenBounds">
/// The one screen the session may use, in physical pixels. Realtime translation is deliberately
/// single-screen: every block then shares one DPI and one capture path, and the loop never has to
/// reason about a window that straddles two monitors at different scales.
/// </param>
/// <param name="Provider">
/// The engine this session translates with. Carried on the request rather than read from settings
/// because 即時翻譯 keeps its choices to itself — see RealtimePage.
/// </param>
/// <param name="TextColor">
/// Subtitle colours as "#RRGGBB", carried here like everything else this sitting runs with. They are
/// read from the settings file rather than chosen per session — see AppSettings — but they still
/// arrive on the request, because a session cannot be asked to change them halfway: reaching the
/// page that sets them means the shell window, and a running session has hidden it.
/// </param>
/// <param name="ScrimOpacity">
/// How opaque the band behind the text is, 0–100. Travels with the colours for the same reason and
/// under the same rule.
/// </param>
/// <param name="NaturalBackground">
/// Whether to repair the picture under the source line instead of drawing the band — 顯示外觀 →
/// 進階選項. Off unless asked for; see AppSettings for why that is the honest default. Travels here
/// under the same rule as the colours: the page that sets it is behind the shell window a running
/// session has hidden.
/// </param>
/// <param name="SampleSourceTextColor">
/// Whether to draw the translation in a colour read off the source line rather than in
/// <paramref name="TextColor"/>. Independent of <paramref name="NaturalBackground"/> — the two fail
/// in different places, so either can be had without the other.
/// </param>
public sealed record RealtimeStartRequest(
    System.Drawing.Rectangle ScreenBounds,
    int MaxBlocks,
    string SourceLanguage,
    string TargetLanguage,
    Models.TranslationProvider Provider,
    string TextColor,
    string ScrimColor,
    int ScrimOpacity,
    bool NaturalBackground,
    bool SampleSourceTextColor);

/// <summary>
/// Owns a realtime session end to end: the edit layer, the per-block overlays, the floating control
/// and the polling loop, plus the shell window it hid to get out of the way.
/// </summary>
/// <remarks>
/// A single instance, like the capture session in <see cref="MainWindow"/> and for the same reason:
/// these windows are Topmost and cover the screen, so a second set would be unclosable furniture on
/// top of the first. It runs on the shared engines in <see cref="AppServices"/> rather than creating
/// its own — a second <see cref="OcrService"/> would load a second copy of the ONNX runtime, and the
/// two features would then fight over the CPU instead of queueing.
/// </remarks>
internal sealed class RealtimeSessionController
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static RealtimeSessionController Instance { get; } = new();

    private RealtimeStartRequest? _request;
    private RealtimeControlWindow? _control;
    private RealtimeEditWindow? _edit;
    private RealtimeTranslationSession? _session;
    private GlobalEscapeHook? _escapeHook;
    private Window? _hiddenShell;

    private readonly Dictionary<int, RealtimeBlockWindow> _blockWindows = [];
    private List<RealtimeBlockPlacement> _blocks = [];
    // UI updates are dispatched asynchronously. Carrying the session's generation here prevents an
    // update queued before a pause from restoring text after the screen was cleared.
    private int _visibleGeneration;

    /// <summary>
    /// The layout the last sitting ended with, offered back to the next one.
    /// </summary>
    /// <remarks>
    /// Ending a session is not usually the user finishing — it is them going back to change the
    /// language, the engine or a colour, all of which live on a page a running session has hidden.
    /// Redrawing three blocks by hand every time they touch one of those is the tax that made this
    /// worth keeping, and the modes go with the rectangles because re-answering 字幕 / 對話 vs 遊戲 / UI for
    /// each block is the same tax again.
    ///
    /// Held here and not written to the settings file, which is the line RealtimePage draws for
    /// everything else about a sitting: this survives 結束即時翻譯 and nothing more. The screen it
    /// was drawn on is kept alongside it because these are physical pixels on one monitor — offered
    /// back for a different screen, or the same one at a different resolution, they would be blocks
    /// the user never drew, in places nothing is.
    /// </remarks>
    private RememberedBlocks? _remembered;

    private sealed record RememberedBlocks(
        System.Drawing.Rectangle Screen, IReadOnlyList<RealtimeBlockPlacement> Blocks);

    /// <summary>
    /// Keeps the layers at the front of the topmost band — see <see cref="AlwaysOnTop"/> for why
    /// they slide behind other topmost windows without it, and why they cannot simply activate
    /// themselves instead.
    /// </summary>
    /// <remarks>
    /// A second is short enough that being covered is a blink rather than a state the user has to
    /// do something about, and long enough that the cost is a handful of SetWindowPos calls — no
    /// redraw, no allocation, nothing that touches the session.
    /// </remarks>
    private readonly DispatcherTimer _stayOnTop = new() { Interval = TimeSpan.FromSeconds(1) };

    private RealtimeSessionController()
    {
        _stayOnTop.Tick += (_, _) =>
        {
            // The control bar last, so it ends up above the block layers it may overlap.
            foreach (var block in _blockWindows.Values) AlwaysOnTop.Reassert(block);
            if (_edit is { } edit) AlwaysOnTop.Reassert(edit);
            if (_control is { } control) AlwaysOnTop.Reassert(control);
        };
    }

    /// <summary>Raised when the session starts or ends, so the page can re-render its controls.</summary>
    public event EventHandler? StateChanged;

    public bool IsActive => _control != null;

    /// <summary>
    /// True while the polling loop is running, as opposed to the user framing blocks. The edit layer
    /// is the whole difference: <see cref="EnterEditMode"/> creates it and <see cref="StartTranslating"/>
    /// closes it, so its absence during a session is what "translating" means here.
    /// </summary>
    public bool IsTranslating => IsActive && _edit is null;

    /// <summary>
    /// Starts a session and drops straight into edit mode. <paramref name="shellToHide"/> is put
    /// away for the duration and brought back by <see cref="Stop"/> — the user is about to frame
    /// something on the screen the shell is sitting on.
    /// </summary>
    public void Start(RealtimeStartRequest request, Window? shellToHide)
    {
        if (IsActive) return;

        Log.Info(
            "Realtime session starting on screen {Screen}, max {Max} block(s), {Src}->{Tgt}",
            request.ScreenBounds, request.MaxBlocks, request.SourceLanguage, request.TargetLanguage);

        _request = request;
        _blocks = _remembered is { } kept && kept.Screen == request.ScreenBounds
            ? [.. kept.Blocks]
            : [];
        _hiddenShell = shellToHide;
        _hiddenShell?.Hide();

        // The same two engines the capture side uses — see AppServices. There is no fallback to get
        // wrong any more: nothing here has to find a window first, so there is no path on which a
        // second inference runtime could be built by accident.
        _session = new RealtimeTranslationSession(AppServices.Ocr, AppServices.Translation);
        Volatile.Write(ref _visibleGeneration, 0);
        _session.RegionUpdated += OnRegionUpdated;
        _session.Failed += OnSessionFailed;
        _session.BusyChanged += OnBusyChanged;

        var control = new RealtimeControlWindow(request.ScreenBounds);
        control.StartRequested += (_, _) => StartTranslating();
        control.EditRequested += (_, _) => EnterEditMode();
        control.CloseRequested += (_, _) => Stop();
        control.ShotRequested += (_, _) => CaptureShowcase();
        control.PauseToggleRequested += (_, _) => TogglePause();
        _control = control;
        control.Show();

        EnterEditMode();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts every layer back at the front of the topmost band, on demand.
    /// </summary>
    /// <remarks>
    /// The way out of the one case the timer cannot win on its own: another topmost window that
    /// re-asserts itself just as persistently, leaving the control bar unreachable and the session
    /// unstoppable — the running session deliberately has no global Esc, because it would swallow
    /// the key from the game underneath. Reached by clicking the tray icon, which does nothing
    /// useful during a session anyway: it opens the translation window, and that window cannot be
    /// used while the layers own the screen.
    /// </remarks>
    public void BringToFront()
    {
        if (!IsActive) return;

        foreach (var block in _blockWindows.Values) AlwaysOnTop.Reassert(block);
        if (_edit is { } edit) AlwaysOnTop.Reassert(edit);
        if (_control is { } control)
        {
            AlwaysOnTop.Reassert(control);
            // Says the click did something even when nothing was covering the bar, which is the
            // more common case: a user who tries this is looking for a sign of life.
            control.ShowMessage(LocalizationService.Get("S.Realtime.MovedToFront"));
        }

        Log.Info("Realtime layers re-asserted on top by request");
    }

    /// <summary>
    /// Stops or restarts the watching, without ending the session — see
    /// <see cref="RealtimeTranslationSession.Pause"/> for what a pause gives back.
    /// </summary>
    /// <remarks>
    /// One entry point for both directions, because both callers are toggles: one button and one
    /// shortcut, each pressed by a user who can see on the bar which of the two states they are in.
    ///
    /// Does nothing while the user is framing blocks: there is nothing running to pause, and the
    /// caller (the capture shortcut) is deliberately silent in that mode rather than explaining a
    /// rule about a feature the user has not started.
    /// </remarks>
    /// <returns>Whether there was a running session to toggle.</returns>
    public bool TogglePause()
    {
        if (!IsTranslating || _session is null || _control is not { } control) return false;

        if (_session.IsPaused)
        {
            // Before the loops start, so the first result cannot arrive to a bar still saying 已暫停.
            control.SetPaused(false);
            _session.Resume();
            return true;
        }

        var pauseGeneration = _session.Pause();
        Volatile.Write(ref _visibleGeneration, pauseGeneration);

        // The whole point of the feature: 暫停 takes the words and their scrims off the screen
        // rather than freezing them there, because a frozen translation over a scene that has moved
        // on is worse than none. Updates queued before this carry an older generation and
        // OnRegionUpdated refuses to put them back.
        foreach (var window in _blockWindows.Values)
            window.SetLines([]);

        control.SetPaused(true);
        return true;
    }

    public void Stop()
    {
        if (!IsActive) return;

        Log.Info("Realtime session ending");

        _stayOnTop.Stop();
        DisposeEscapeHook();

        if (_session != null)
        {
            _session.RegionUpdated -= OnRegionUpdated;
            _session.Failed -= OnSessionFailed;
            _session.BusyChanged -= OnBusyChanged;

            // The one press that means "done with this": the recogniser's memory goes back here
            // rather than on 暫停, which means "not now" and is followed by 繼續 wanting it again.
            _session.Stop(releaseRecogniser: true);
            _session = null;
        }

        CloseBlockWindows();
        CloseEditWindow();

        CloseWindow(_control, nameof(RealtimeControlWindow));
        _control = null;

        // Kept before the working copy is dropped — see RememberedBlocks for why this one thing
        // outlives the session while everything else here is torn down.
        _remembered = _request is { } ended && _blocks.Count > 0
            ? new RememberedBlocks(ended.ScreenBounds, _blocks)
            : null;

        _blocks = [];
        _request = null;

        RestoreShell();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops the layout kept from the last sitting, so the next one starts on an empty screen.
    /// </summary>
    /// <remarks>
    /// Called when the block count changes, and when the translation window closes. Both are the
    /// same statement: what was drawn was drawn for a set-up that no longer exists. The count is
    /// not checked when the blocks are offered back, deliberately — the user asked for them to go
    /// the moment the count moved, and 2 → 3 → 2 leaving old blocks behind would be a set-up they
    /// had told the program to forget reappearing because they changed their mind twice.
    ///
    /// Only the memory. A session that happens to be running keeps its own blocks and carries on:
    /// closing the shell window has never been a request to end a session, and this does not make
    /// it one.
    /// </remarks>
    public void ForgetBlocks() => _remembered = null;

    // ── Modes ────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (_request is not { } request || _control is not { } control) return;

        _session?.Stop();
        CloseBlockWindows();
        CloseEditWindow();

        // Read here rather than carried on the request: the user changes it from inside this layer,
        // and every later trip through edit mode should open on the answer they gave last.
        var settings = SettingsService.Instance;

        var edit = new RealtimeEditWindow(
            request.ScreenBounds, _blocks, request.MaxBlocks, settings.Current.RealtimeGuidanceExpanded);
        edit.BlocksChanged += (_, _) =>
        {
            _blocks = [.. edit.GetPhysicalBlocks()];
            control.SetBlockCount(_blocks.Count, request.MaxBlocks);
        };
        // Written on the press rather than at the end of the session: a session over a full-screen
        // game is as likely to end with the machine being shut down as with 結束即時翻譯.
        edit.GuidanceExpandedChanged += (_, expanded) =>
        {
            if (settings.Current.RealtimeGuidanceExpanded == expanded) return;
            settings.Current.RealtimeGuidanceExpanded = expanded;
            settings.Save();
        };
        edit.LimitReached += (_, _) =>
            control.ShowMessage(LocalizationService.Format("S.Realtime.TooManyBlocks", request.MaxBlocks));
        _edit = edit;
        edit.Show();

        // Ownership, not a Topmost nudge: the edit layer covers the whole screen, so any moment it
        // is above the control the user's clicks on the bar land on the drawing canvas instead —
        // which reads as the buttons having stopped working. Re-asserting Topmost puts the control
        // back on top once; an owned window is guaranteed to stay above its owner no matter what is
        // clicked afterwards. CloseEditWindow clears this again before closing the owner, or the
        // control would be closed along with it.
        control.Owner = edit;

        control.SetMode(RealtimeControlMode.Edit);
        control.SetBlockCount(_blocks.Count, request.MaxBlocks);
        // Belt and braces alongside the ownership above: cheap, and the failure it guards against is
        // a control bar the user cannot reach at all.
        control.BringToFront();

        _stayOnTop.Start();

        // Only while editing. The hook swallows Esc process-wide, and a translating session can run
        // for hours next to an application that needs its own Esc key.
        DisposeEscapeHook();
        _escapeHook = GlobalEscapeHook.Install(Stop);
    }

    private void StartTranslating()
    {
        if (_request is not { } request || _control is not { } control || _session is null) return;

        if (_blocks.Count == 0)
        {
            control.ShowMessage(LocalizationService.Get("S.Realtime.NeedOneBlock"));
            return;
        }

        DisposeEscapeHook();
        CloseEditWindow();

        var regions = _blocks
            .Select((block, index) => new RealtimeRegion(index, block.Bounds, block.Mode))
            .ToList();

        foreach (var region in regions)
        {
            var window = new RealtimeBlockWindow(
                region.Id, region.Bounds, request.SourceLanguage, request.TargetLanguage,
                request.TextColor, request.ScrimColor, request.ScrimOpacity,
                request.NaturalBackground, request.SampleSourceTextColor);
            _blockWindows[region.Id] = window;
            window.Show();
        }

        // Set before the mode switch renders, so the capsule appears with the pair already in it
        // rather than showing the placeholder for a frame.
        control.SetLanguages(request.SourceLanguage, request.TargetLanguage);
        control.SetMode(RealtimeControlMode.Running);
        control.BringToFront();
        _stayOnTop.Start();

        _session.Start(regions, request.SourceLanguage, request.TargetLanguage, request.Provider);
    }

    /// <summary>
    /// Builds a picture of the screen with this session's subtitles drawn onto it, and puts it on
    /// the clipboard — the only way to show someone what this feature does, since the layers
    /// themselves are excluded from every form of screen capture.
    /// </summary>
    /// <remarks>
    /// Saved to disk as well when 截圖 → 截圖時自動儲存 is on, deliberately following the rule the
    /// capture side already set rather than introducing a second one for the same kind of output.
    /// </remarks>
    private void CaptureShowcase()
    {
        if (_control is not { } control || _request is not { } request) return;

        try
        {
            var overlays = _blockWindows.Values
                .Select(window => (Window: window, Image: window.RenderForCapture()))
                .Where(pair => pair.Image is not null)
                .Select(pair => new RealtimeShowcaseCapture.Overlay(
                    pair.Window.PhysicalBounds, pair.Image!))
                .ToList();

            if (overlays.Count == 0)
            {
                // Composing here would produce a plain screenshot, which is not what was asked for
                // and gives no sign that anything was missing.
                control.ShowMessage(LocalizationService.Get("S.Realtime.NothingToCapture"));
                return;
            }

            // Last, so it lands on top of any block it overlaps — the same order it has on screen.
            // Included because a picture of subtitles with no visible tool looks like the application
            // being watched simply has subtitles, which is the opposite of what this capture is for.
            if (control.RenderForCapture() is { } bar)
                overlays.Add(new RealtimeShowcaseCapture.Overlay(control.PhysicalBounds, bar));

            var image = RealtimeShowcaseCapture.Compose(request.ScreenBounds, overlays);
            if (image is null)
            {
                control.ShowMessage(LocalizationService.Get("S.Realtime.CaptureFailed"), RealtimeMessageKind.Failure);
                return;
            }

            System.Windows.Clipboard.SetImage(image);

            var settings = SettingsService.Instance.Current;
            if (!settings.SaveScreenshotToDisk)
            {
                control.ShowMessage(LocalizationService.Get("S.Realtime.CaptureCopied"));
                return;
            }

            var path = ScreenshotSaveService.Save(image, settings.ScreenshotSavePath);
            Log.Info("Realtime showcase capture saved to {Path}", path);
            control.ShowMessage(LocalizationService.Get("S.Realtime.CaptureCopiedAndSaved"));
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process and saving can hit a full or read-only
            // folder. Neither is worth ending a session over, and the bar is where the user is
            // looking.
            Log.Warn(ex, "Realtime showcase capture failed");
            control.ShowMessage(LocalizationService.Format("S.Realtime.CaptureError", ex.Message), RealtimeMessageKind.Failure);
        }
    }

    // ── Session callbacks (raised on the polling thread) ─────────────────────────────────────────

    private void OnRegionUpdated(object? sender, RealtimeRegionUpdate update) =>
        OnDispatcher(() =>
        {
            if (update.Generation != Volatile.Read(ref _visibleGeneration)) return;

            // The user may have gone back to edit mode while this pass was in flight; its window is
            // gone and the result belongs to a layout that no longer exists.
            if (_blockWindows.TryGetValue(update.RegionId, out var window))
                window.SetLines(update.Lines);
        });

    private void OnSessionFailed(object? sender, string message) =>
        OnDispatcher(() => _control?.ShowMessage(message, RealtimeMessageKind.Failure));

    private void OnBusyChanged(object? sender, bool busy) =>
        OnDispatcher(() => _control?.SetBusy(busy));

    private static void OnDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    // ── Teardown helpers ─────────────────────────────────────────────────────────────────────────

    private void CloseEditWindow()
    {
        // Before the close, always: WPF closes owned windows with their owner, so leaving this set
        // would take the control bar down with the edit layer.
        if (_control is { } control && ReferenceEquals(control.Owner, _edit))
            control.Owner = null;

        CloseWindow(_edit, nameof(RealtimeEditWindow));
        _edit = null;
    }

    private void CloseBlockWindows()
    {
        foreach (var window in _blockWindows.Values)
            CloseWindow(window, nameof(RealtimeBlockWindow));
        _blockWindows.Clear();
    }

    private void DisposeEscapeHook()
    {
        _escapeHook?.Dispose();
        _escapeHook = null;
    }

    private void RestoreShell()
    {
        var shell = _hiddenShell;
        _hiddenShell = null;
        if (shell is null || shell.IsVisible) return;

        try
        {
            shell.Show();
            shell.Activate();
        }
        catch (Exception ex)
        {
            // Racing an app shutdown that already destroyed the window — nothing left to restore,
            // and it must not take the rest of the teardown down with it.
            Log.Warn(ex, "Could not restore the shell window after a realtime session");
        }
    }

    // Each window is closed independently: these are Topmost layers over the user's screen, so a
    // throwing Close() must not strand the ones after it.
    private static void CloseWindow(Window? window, string name)
    {
        if (window is null) return;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to close {Window} — forcing teardown of the remaining windows", name);
        }
    }
}
