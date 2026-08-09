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
public sealed record RealtimeStartRequest(
    System.Drawing.Rectangle ScreenBounds,
    int MaxBlocks,
    string SourceLanguage,
    string TargetLanguage,
    Models.TranslationProvider Provider,
    string TextColor,
    string ScrimColor);

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
        _blocks = [];
        _hiddenShell = shellToHide;
        _hiddenShell?.Hide();

        // The same two engines the capture side uses — see AppServices. There is no fallback to get
        // wrong any more: nothing here has to find a window first, so there is no path on which a
        // second inference runtime could be built by accident.
        _session = new RealtimeTranslationSession(AppServices.Ocr, AppServices.Translation);
        _session.RegionUpdated += OnRegionUpdated;
        _session.Failed += OnSessionFailed;
        _session.BusyChanged += OnBusyChanged;

        var control = new RealtimeControlWindow(request.ScreenBounds);
        control.StartRequested += (_, _) => StartTranslating();
        control.EditRequested += (_, _) => EnterEditMode();
        control.CloseRequested += (_, _) => Stop();
        control.ShotRequested += (_, _) => CaptureShowcase();
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
            control.ShowMessage("已將即時翻譯視窗移至最上層");
        }

        Log.Info("Realtime layers re-asserted on top by request");
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
            _session.Stop();
            _session = null;
        }

        CloseBlockWindows();
        CloseEditWindow();

        CloseWindow(_control, nameof(RealtimeControlWindow));
        _control = null;

        _blocks = [];
        _request = null;

        RestoreShell();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Modes ────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (_request is not { } request || _control is not { } control) return;

        _session?.Stop();
        CloseBlockWindows();
        CloseEditWindow();

        var edit = new RealtimeEditWindow(request.ScreenBounds, _blocks, request.MaxBlocks);
        edit.BlocksChanged += (_, _) =>
        {
            _blocks = [.. edit.GetPhysicalBlocks()];
            control.SetBlockCount(_blocks.Count, request.MaxBlocks);
        };
        edit.LimitReached += (_, _) =>
            control.ShowMessage($"最多同時 {request.MaxBlocks} 個區塊，請先移除一個");
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
            control.ShowMessage("請先拖曳建立至少一個區塊");
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
                request.TextColor, request.ScrimColor);
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
                control.ShowMessage("目前還沒有譯文可以擷取");
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
                control.ShowMessage("擷取畫面失敗，請再試一次", RealtimeMessageKind.Failure);
                return;
            }

            System.Windows.Clipboard.SetImage(image);

            var settings = SettingsService.Instance.Current;
            if (!settings.SaveScreenshotToDisk)
            {
                control.ShowMessage("畫面截圖已複製到剪貼簿");
                return;
            }

            var path = ScreenshotSaveService.Save(image, settings.ScreenshotSavePath);
            Log.Info("Realtime showcase capture saved to {Path}", path);
            control.ShowMessage("畫面截圖已複製到剪貼簿並儲存至本機");
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process and saving can hit a full or read-only
            // folder. Neither is worth ending a session over, and the bar is where the user is
            // looking.
            Log.Warn(ex, "Realtime showcase capture failed");
            control.ShowMessage($"擷取失敗：{ex.Message}", RealtimeMessageKind.Failure);
        }
    }

    // ── Session callbacks (raised on the polling thread) ─────────────────────────────────────────

    private void OnRegionUpdated(object? sender, RealtimeRegionUpdate update) =>
        OnDispatcher(() =>
        {
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
