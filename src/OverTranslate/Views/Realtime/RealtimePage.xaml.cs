using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Screen = System.Windows.Forms.Screen;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>One monitor, as offered in the screen picker.</summary>
public sealed record ScreenItem(
    string DeviceName, string Display, System.Drawing.Rectangle Bounds, bool IsPrimary);

/// <summary>One open window, as offered in the source picker.</summary>
/// <remarks>
/// The handle is the value, and it is only good for as long as that window is open — which is why
/// the list is rebuilt on demand rather than kept, and why the controller checks the handle again
/// before it builds anything around it.
/// </remarks>
public sealed record WindowItem(IntPtr Hwnd, string Display, string Detail);

/// <summary>
/// Sets up a realtime session and then gets out of the way — everything after 選取翻譯區塊 happens on
/// the screen itself, driven by <see cref="RealtimeSessionController"/>.
/// </summary>
/// <remarks>
/// The page holds two kinds of value, and the line between them is the rule to keep:
///
/// <para><b>Parameters of one sitting</b> — capture source, screen and block count — are not read
/// from or written to the settings file. Reopening the window must not restore a screen that may
/// have been unplugged, or blocks that no longer frame the same content. The capture source is the
/// clearest case of the rule: 指定視窗 is a live window handle, which does not survive the window
/// being closed, let alone the next launch, so storing the mode alone would reopen the page in a
/// state missing its own answer.</para>
///
/// <para><b>Translation preferences</b> — both languages and the service — are stored independently
/// from screenshot and text translation. Watching a game and translating a document are different
/// jobs with different latency and quality requirements, but choosing the same realtime pair and
/// service on every launch is needless repetition. The source language joined them late, and it is
/// the one with a cost: someone who watched Japanese yesterday and English today starts on the wrong
/// one, and realtime recognition gives no sign of a mismatch beyond reading badly. It is offered
/// back anyway because it is a language the user picked, not one guessed for them — 自動 is not in
/// this page's picker at all — and re-picking it every sitting is the repetition this group exists
/// to remove.</para>
///
/// <para><b>Appearance preferences</b> — the subtitle's two colours and how opaque its band is, in
/// 顯示外觀 — are stored and restored. A reader who needs yellow on dark blue needs it every session,
/// and re-picking it each time is the same failure as forgetting a theme. They live here rather than
/// in 設定 so the control sits beside what it changes.</para>
///
/// <para>Nothing on screen currently marks which card is which; 翻譯區塊 states that its value is not
/// kept, and 顯示外觀 says nothing either way. That is a deliberate trade for a shorter card, not an
/// oversight — if it turns out to confuse anyone, the fix is a line in 顯示外觀 saying its values are
/// kept, not removing the note from 翻譯區塊.</para>
///
/// <para><b>Kept for as long as this window is open</b> — the blocks the user drew, with the mode
/// chosen for each. Not a preference and not a parameter: it is the answer to "where is the text",
/// which is worth several minutes of dragging and is thrown away the moment the question changes.
/// It lives in <see cref="RealtimeSessionController"/> rather than here, and this page tells it to
/// forget in the two cases that change the question — the block count moving, and this window
/// closing.</para>
///
/// Anything added later belongs to one of these three groups; if it is not obvious which, it is a
/// preference only when the user would be annoyed to set it twice, and a parameter only when
/// offering it back could be wrong rather than merely stale.
/// </remarks>
public partial class RealtimePage : UserControl
{
    /// <summary>
    /// 繁體中文, because this page's own reason to exist is watching foreign content, and the reader
    /// is the person sitting here.
    /// </summary>
    private const string DefaultTargetLanguage = "ZH-HANT";

    /// <summary>
    /// Microsoft, chosen for this page rather than inherited: measured over 639 realtime passes it
    /// answered in 82ms at the median, which is what makes a subtitle appear to keep up. A service
    /// that reads more carefully but takes a second is the right default elsewhere and the wrong one
    /// here.
    /// </summary>
    private const TranslationProvider DefaultProvider = TranslationProvider.Microsoft;

    // Shared with the shortcut path, which has to offer the same range without this page open.
    private const int MinBlocks = RealtimeQuickStart.MinBlocks;
    private const int MaxBlocks = RealtimeQuickStart.MaxBlocks;

    private int _blockCount = MinBlocks;

    // Deliberately not stored, and not for the usual reason. The mode could be remembered; the
    // window it needs cannot — a handle is meaningless in the next sitting — so restoring 指定視窗
    // would reopen the page in a state that is missing its own answer, which is worse than the one
    // extra press. See the class remarks for the line this sits on.
    private bool _windowsLoaded;

    // True while this page is writing the opacity slider rather than the user, so that restoring a
    // stored value does not read as a change and write it straight back.
    private bool _syncingOpacity;

    // True between the thumb being picked up and put down. The preview follows every step of a drag;
    // the settings file waits for the end of it, because a drag across the track is one decision and
    // would otherwise be a hundred writes.
    private bool _draggingOpacity;

    public RealtimePage()
    {
        InitializeComponent();

        BindPickers();

        ApplyPageDefaults();
        ApplyCaptureMode();

        // Attached after the initial value is set, so initialisation does not fire the handler
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
        TgtLangBox.SelectionChanged += TgtLangBox_SelectionChanged;
        SrcLangBox.SelectionChanged += SrcLangBox_SelectionChanged;

        // The slider's own drag events, which it forwards from its thumb rather than surfacing as
        // properties of its own.
        ScrimOpacitySlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _draggingOpacity = true));
        ScrimOpacitySlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _draggingOpacity = false;
                PersistScrimOpacity();
            }));

        // Set before the handlers go on, for the reason the boxes above give: restoring a stored
        // value must not read as the user reaching for the switch and write it straight back.
        var appearance = SettingsService.Instance.Current;
        NaturalBackgroundToggle.IsChecked = appearance.RealtimeNaturalBackgroundEnabled;
        SampleTextColorToggle.IsChecked = appearance.RealtimeSampleSourceTextColor;
        NaturalBackgroundToggle.Checked += NaturalBackgroundToggle_Toggled;
        NaturalBackgroundToggle.Unchecked += NaturalBackgroundToggle_Toggled;
        SampleTextColorToggle.Checked += SampleTextColorToggle_Toggled;
        SampleTextColorToggle.Unchecked += SampleTextColorToggle_Toggled;

        SyncScrimOpacity();
        RenderColours();
        RenderPauseHint();

        RealtimeSessionController.Instance.StateChanged += OnSessionStateChanged;

        // Not just the stepper: the start button has to begin unavailable too, because 原文語言
        // starts unset and RenderState is what ties the two together.
        RenderState();
    }

    /// <summary>
    /// Applies this page's independent translation preferences and per-session defaults.
    /// </summary>
    /// <remarks>
    /// 原文語言 comes back only if the user has ever set it. Empty is the state that asks the question
    /// rather than answering it badly: realtime recognition gets one look at a frame and no retry, so
    /// there is no silent automatic mode to fall back on here — which is why 自動 is cleared rather
    /// than offered. <see cref="LanguageData.GetValidOcrSourceCode"/> answers 自動 for anything it
    /// cannot place, so that one branch covers a never-set value, a hand-edited one and a code
    /// retired since.
    /// </remarks>
    private void ApplyPageDefaults()
    {
        var settings = SettingsService.Instance.Current;
        // Never null any more: the picker used to be left blank so that only a language the user
        // chose was ever used, and the shortcut has no page on which to ask. Anything unusable —
        // blank from an older settings file, 自動 from a hand-edited one — resolves to the default.
        SrcLangBox.SelectedValue =
            LanguageData.GetValidRealtimeSourceCode(settings.RealtimeSourceLanguage);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(
            settings.RealtimeTargetLanguage);
        if (TgtLangBox.SelectedValue == null)
            TgtLangBox.SelectedValue = DefaultTargetLanguage;

        ProviderBox.SelectedValue = settings.RealtimeProvider;
        if (ProviderBox.SelectedValue == null)
            ProviderBox.SelectedValue = DefaultProvider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        RenderProviderHint();

        // Clamped rather than trusted: this is a settings-file value and the stepper's range is the
        // one that has to hold. RenderBlockCount runs later, from Reload.
        _blockCount = Math.Clamp(settings.RealtimeBlockCount, MinBlocks, MaxBlocks);
    }

    /// <summary>
    /// Re-reads what may have changed while the user was elsewhere: the attached monitors, and
    /// whether a session is running.
    /// </summary>
    /// <summary>
    /// Fills the three language and provider pickers.
    /// </summary>
    /// <remarks>
    /// Re-run on every <see cref="Reload"/> rather than only at construction, because the item
    /// labels come from the string dictionary and the shell keeps this page for its lifetime —
    /// built once, it would still be showing the language the app started in. The selections are
    /// carried across by hand, because rebinding the items drops them — and dropping them here would
    /// read as this page forgetting what the user chose the moment they changed the interface
    /// language.
    /// </remarks>
    private void BindPickers()
    {
        var source   = SrcLangBox.SelectedValue;
        var target   = TgtLangBox.SelectedValue;
        var provider = ProviderBox.SelectedValue;

        LocalizationService.BindLocalizedItems(
            SrcLangBox,
            LanguageData.OcrSourceLanguages
                .Where(language => !LanguageData.IsAutomaticSource(language.Code))
                .ToList());
        LocalizationService.BindLocalizedItems(TgtLangBox,  LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

        SrcLangBox.SelectedValue  = source;
        TgtLangBox.SelectedValue  = target;
        ProviderBox.SelectedValue = provider;
    }

    public void Reload()
    {
        BindPickers();
        LoadScreens();
        // Both halves: the mode's own explanation comes from the string dictionary, and the window
        // list is a snapshot of a desktop the user has been away from since it was taken.
        ApplyCaptureMode();
        if (_windowsLoaded) LoadWindows();
        SyncScrimOpacity();
        RenderColours();
        RenderPauseHint();
        RenderState();
    }

    /// <summary>
    /// Names the capture shortcut in 運作方式, or drops the line when there is none to name.
    /// </summary>
    /// <remarks>
    /// Re-read on every <see cref="Reload"/> rather than only at construction: 設定 is where that
    /// shortcut is changed, and the user reaches this page again through the same nav rail — so the
    /// line would otherwise go on naming the key they just replaced.
    /// </remarks>
    private void RenderPauseHint()
    {
        PauseHint.Text = RealtimePauseHint.ForSettingsPage(RealtimePauseHint.CurrentHotkey);
        PauseHint.Visibility = PauseHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Detaches from the controller when the shell window is destroyed.</summary>
    /// <remarks>
    /// The kept block layout goes with the window, which is the whole of its scope: it exists so
    /// that ending a session to change something on this page does not cost the user their blocks,
    /// and closing the window is the end of that errand. A session already running is left alone —
    /// it owns its own blocks, and closing this window has never been a request to stop it.
    /// </remarks>
    public void Teardown()
    {
        RealtimeSessionController.Instance.StateChanged -= OnSessionStateChanged;
        RealtimeSessionController.Instance.ForgetBlocks();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is TranslationProvider provider)
            SaveTranslationPreference(settings => settings.RealtimeProvider = provider);
        RenderProviderHint();
    }

    /// <remarks>
    /// Only a real selection is written, never the momentary null that rebinding the items produces
    /// — see <see cref="BindPickers"/>. Without that guard, changing the interface language would
    /// blank this preference on its way past.
    /// </remarks>
    private void SrcLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SrcLangBox.SelectedValue is string sourceLanguage)
            SaveTranslationPreference(settings =>
                settings.RealtimeSourceLanguage = LanguageData.GetValidOcrSourceCode(sourceLanguage));

        RenderState();
    }

    private void TgtLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TgtLangBox.SelectedValue is not string targetLanguage) return;
        SaveTranslationPreference(settings =>
            settings.RealtimeTargetLanguage = LanguageData.GetValidTargetCode(targetLanguage));
    }

    private static void SaveTranslationPreference(Action<AppSettings> apply)
    {
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
    }

    /// <summary>
    /// The service's own note, plus the one thing that would otherwise only surface as a failure
    /// mid-session: a key-based service with no key saved. Said here, before the screen is handed
    /// over, rather than as an error on a floating bar the user is no longer looking at.
    /// </summary>
    private void RenderProviderHint()
    {
        var item = ProviderBox.SelectedItem as ProviderItem;
        var missingKey = item?.RequiresApiKey == true &&
                         string.IsNullOrWhiteSpace(SettingsService.Instance.Current.ApiKey);

        ProviderHint.Text = missingKey
            ? LocalizationService.Get("S.Realtime.NoApiKey")
            : item?.Hint ?? "";
        ProviderHint.Foreground = missingKey
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }

    // ── 擷取來源 ───────────────────────────────────────────────────────────────

    /// <summary>Whether the user has asked for one window rather than the whole screen.</summary>
    private bool WindowMode => WindowModeRadio.IsChecked == true;

    private void CaptureModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the card exists.
        if (WindowPickerPanel is null) return;

        ApplyCaptureMode();
    }

    /// <summary>
    /// Shows the half of the card the chosen mode needs, and hides the screen picker when it is no
    /// longer the user's to answer.
    /// </summary>
    /// <remarks>
    /// The window list is built the first time 指定視窗 is chosen rather than when the page loads:
    /// enumerating every open window costs a pass over the desktop, and the common case — a user who
    /// wants the whole screen — never needs it. After that it is only rebuilt on request, because a
    /// list that reshuffles itself while the user is reading it is worse than one that is a few
    /// seconds old.
    /// </remarks>
    private void ApplyCaptureMode()
    {
        var window = WindowMode;

        WindowPickerPanel.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        ScreenPanel.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        CaptureSourceHint.Text = LocalizationService.Get(
            window ? "S.Realtime.CaptureSourceWindowHint" : "S.Realtime.CaptureSourceScreenHint");

        if (!window || _windowsLoaded) return;

        _windowsLoaded = true;
        LoadWindows();
    }

    private void RefreshWindowsBtn_Click(object sender, RoutedEventArgs e) => LoadWindows();

    /// <summary>
    /// Asks Windows what is open, keeping the user's pick if it is still there.
    /// </summary>
    private void LoadWindows()
    {
        var previous = (WindowBox.SelectedItem as WindowItem)?.Hwnd ?? IntPtr.Zero;

        var items = CaptureWindowList.Enumerate()
            .Select(window => new WindowItem(
                window.Hwnd,
                window.Title,
                window.ProcessName.Length > 0
                    ? LocalizationService.Format("S.Realtime.CaptureWindowDetail", window.ProcessName)
                    : ""))
            .ToList();

        WindowBox.ItemsSource = items;
        WindowBox.DisplayMemberPath = nameof(WindowItem.Display);

        // Restored by handle rather than by name: two windows of the same application share a title
        // often enough that picking by name would silently move the user to a different one.
        WindowBox.SelectedItem = items.FirstOrDefault(item => item.Hwnd == previous);

        RenderWindowChoice(items.Count);
    }

    private void WindowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowHint is null) return;

        RenderWindowChoice((WindowBox.ItemsSource as IReadOnlyList<WindowItem>)?.Count ?? 0);
    }

    /// <summary>
    /// The line under the picker: which monitor the framing will happen on, or why there is nothing
    /// to choose.
    /// </summary>
    /// <remarks>
    /// Naming the monitor matters more here than it looks. The screen picker is gone in this mode,
    /// so the one thing the user cannot otherwise tell — which screen is about to be covered by a
    /// full-screen framing layer — has to be said out loud, and it is decided by a window they may
    /// have dragged somewhere since they last looked.
    /// </remarks>
    private void RenderWindowChoice(int count)
    {
        WindowPlaceholder.Visibility =
            WindowBox.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;

        if (count == 0)
        {
            WindowHint.Text = LocalizationService.Get("S.Realtime.CaptureWindowNone");
            return;
        }

        if (WindowBox.SelectedItem is not WindowItem item)
        {
            WindowHint.Text = LocalizationService.Get("S.Realtime.CaptureWindowHint");
            return;
        }

        if (ScreenOf(item.Hwnd) is not { } screen)
        {
            // Closed while the list was sitting open. Said here rather than saved for 開始翻譯,
            // because the user is looking at this line right now.
            WindowHint.Text = LocalizationService.Get("S.Realtime.WindowCaptureGone");
            return;
        }

        var line = LocalizationService.Format("S.Realtime.CaptureWindowOnScreen", ScreenLabel(screen));
        WindowHint.Text = item.Detail.Length > 0 ? $"{line} {item.Detail}" : line;
    }

    /// <summary>The monitor a window sits on, or null when it has gone since the list was built.</summary>
    private static Screen? ScreenOf(IntPtr hwnd)
    {
        try
        {
            return CaptureWindowList.StillAvailable(hwnd) ? Screen.FromHandle(hwnd) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The same wording the screen picker uses, so the two name a monitor identically.</summary>
    private static string ScreenLabel(Screen screen)
    {
        var screens = Screen.AllScreens;
        var index = Array.FindIndex(screens, candidate => candidate.DeviceName == screen.DeviceName);

        return LocalizationService.Format(
            screen.Primary ? "S.Realtime.ScreenItemPrimary" : "S.Realtime.ScreenItem",
            index + 1, screen.Bounds.Width, screen.Bounds.Height);
    }

    private void LoadScreens()
    {
        var previous = ScreenBox.SelectedValue as string;

        var screens = Screen.AllScreens;
        var items = screens
            .Select((screen, index) => new ScreenItem(
                screen.DeviceName,
                LocalizationService.Format(
                    screen.Primary ? "S.Realtime.ScreenItemPrimary" : "S.Realtime.ScreenItem",
                    index + 1, screen.Bounds.Width, screen.Bounds.Height),
                screen.Bounds,
                screen.Primary))
            .ToList();

        ScreenBox.ItemsSource = items;
        ScreenBox.DisplayMemberPath = nameof(ScreenItem.Display);

        // Keep the user's pick across a reload, but fall back to the monitor this window is on —
        // which is the one they are looking at, and not necessarily the primary.
        if (previous != null && items.Any(item => item.DeviceName == previous))
            ScreenBox.SelectedValue = previous;
        else
            ScreenBox.SelectedValue = CurrentScreenDeviceName(items);
    }

    private string? CurrentScreenDeviceName(IReadOnlyList<ScreenItem> items)
    {
        try
        {
            if (Window.GetWindow(this) is { } window)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    var deviceName = Screen.FromHandle(handle).DeviceName;
                    if (items.Any(item => item.DeviceName == deviceName))
                        return deviceName;
                }
            }
        }
        catch (Exception)
        {
            // Window not yet sourced, or a monitor removed mid-call — the primary below is a fine
            // answer either way.
        }

        return items.FirstOrDefault(item => item.IsPrimary)?.DeviceName
            ?? items.FirstOrDefault()?.DeviceName;
    }

    private void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        var controller = RealtimeSessionController.Instance;
        if (controller.IsActive)
        {
            controller.Stop();
            return;
        }

        // The other half of the same rule the capture side enforces: one OCR engine, one pool of
        // inference slots, and neither feature is any use with the other competing for them. This
        // way round is the less likely of the two — a capture covers the screen, so its selection
        // layer is usually over this button — but the toolbar phase leaves the screen usable.
        if (System.Windows.Application.Current.MainWindow is MainWindow { IsCapturing: true })
        {
            SetStatus(LocalizationService.Get("S.Realtime.CaptureInProgress"), isError: true);
            return;
        }

        if (SrcLangBox.SelectedValue is not string sourceLanguage)
        {
            SetStatus(LocalizationService.Get("S.Realtime.ChooseSourceFirst"), isError: true);
            return;
        }

        // The two modes need different things to be true before there is a session to start, and
        // both failures are the user's to fix on this page rather than something to discover on a
        // screen that has just gone black.
        System.Drawing.Rectangle framingBounds;
        var sourceWindow = IntPtr.Zero;

        if (WindowMode)
        {
            if (WindowBox.SelectedItem is not WindowItem window)
            {
                SetStatus(LocalizationService.Get("S.Realtime.ChooseWindowFirst"), isError: true);
                return;
            }

            // Checked here and again in the controller, and neither is redundant: this one can still
            // tell the user to refresh the list, while the later one is the last word before pixels
            // are read.
            if (ScreenOf(window.Hwnd) is not { } windowScreen)
            {
                SetStatus(LocalizationService.Get("S.Realtime.WindowCaptureGone"), isError: true);
                LoadWindows();
                return;
            }

            // The blocks have to be drawn over the window, so the framing layer belongs on whatever
            // monitor that window is on — not on whichever one the hidden picker last held.
            framingBounds = windowScreen.Bounds;
            sourceWindow = window.Hwnd;
        }
        else
        {
            if (ScreenBox.SelectedItem is not ScreenItem screen)
            {
                SetStatus(LocalizationService.Get("S.Realtime.NoScreens"), isError: true);
                return;
            }

            framingBounds = screen.Bounds;
        }

        var settings = SettingsService.Instance.Current;
        var request = new RealtimeStartRequest(
            framingBounds,
            _blockCount,
            LanguageData.GetValidOcrSourceCode(sourceLanguage),
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string),
            ProviderBox.SelectedValue as TranslationProvider? ?? DefaultProvider,
            settings.RealtimeTextColor,
            settings.RealtimeScrimColor,
            settings.RealtimeScrimOpacity,
            settings.RealtimeNaturalBackgroundEnabled,
            settings.RealtimeSampleSourceTextColor,
            WindowMode ? RealtimeCaptureMode.Window : RealtimeCaptureMode.Screen,
            sourceWindow);

        // The shell is handed over to be hidden: it is almost certainly sitting on the screen the
        // user is about to frame blocks on, and it comes back when the session ends.
        controller.Start(request, Window.GetWindow(this));
    }

    // ── 顯示外觀 ───────────────────────────────────────────────────────────────

    private void TextColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.RealtimeTextColor,
            picked => Persist(s => s.RealtimeTextColor = picked));

    private void ScrimColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.RealtimeScrimColor,
            picked => Persist(s => s.RealtimeScrimColor = picked));

    private void ResetColorsBtn_Click(object sender, RoutedEventArgs e)
    {
        Persist(s =>
        {
            s.RealtimeTextColor = RealtimeSubtitleColors.DefaultText;
            s.RealtimeScrimColor = RealtimeSubtitleColors.DefaultScrim;
            s.RealtimeScrimOpacity = RealtimeSubtitleColors.DefaultScrimOpacity;
        });

        // The slider is the one control here that holds its own value rather than reading it back
        // from the settings on every render, so it has to be told.
        SyncScrimOpacity();
        RenderColours();
    }

    /// <summary>
    /// The two 進階選項 switches. Written the moment they are flipped — one press is one decision, so
    /// there is nothing to hold back the way a drag across the opacity track has.
    /// </summary>
    private void NaturalBackgroundToggle_Toggled(object sender, RoutedEventArgs e) =>
        Persist(s => s.RealtimeNaturalBackgroundEnabled = NaturalBackgroundToggle.IsChecked == true);

    /// <inheritdoc cref="NaturalBackgroundToggle_Toggled"/>
    private void SampleTextColorToggle_Toggled(object sender, RoutedEventArgs e) =>
        Persist(s => s.RealtimeSampleSourceTextColor = SampleTextColorToggle.IsChecked == true);

    /// <summary>
    /// Follows the thumb: the label and the preview on every step, the settings file at the end of a
    /// drag — or immediately, when the value moved by keyboard or by a click on the track, neither of
    /// which has an end to wait for.
    /// </summary>
    private void ScrimOpacitySlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingOpacity) return;

        // Persist renders on its way through, so only the held-back case has to render for itself.
        if (_draggingOpacity) RenderColours();
        else PersistScrimOpacity();
    }

    /// <summary>Puts the stored opacity on the slider without that reading as the user setting it.</summary>
    private void SyncScrimOpacity()
    {
        _syncingOpacity = true;
        ScrimOpacitySlider.Value = RealtimeSubtitleColors.ClampOpacity(
            SettingsService.Instance.Current.RealtimeScrimOpacity);
        _syncingOpacity = false;
    }

    private void PersistScrimOpacity() =>
        Persist(s => s.RealtimeScrimOpacity = CurrentScrimOpacity);

    /// <summary>
    /// What the slider is showing, which is what the preview draws — not what is stored, because
    /// mid-drag those are deliberately different.
    /// </summary>
    private int CurrentScrimOpacity =>
        RealtimeSubtitleColors.ClampOpacity((int)Math.Round(ScrimOpacitySlider.Value));

    /// <summary>
    /// Opens the system colour picker on the current value and reports what came back.
    /// </summary>
    /// <remarks>
    /// The native dialog, not one of ours: it already carries a full picker, the custom-colour slots
    /// and every keyboard convention, and none of that is worth rebuilding for two fields. It has no
    /// alpha channel, which suits a setting that has no alpha to offer — the scrim's is fixed, see
    /// <see cref="RealtimeSubtitleColors"/>.
    /// </remarks>
    private void PickColour(string current, Action<string> onPicked)
    {
        var start = RealtimeSubtitleColors.Text(current);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            // Opens on what is in effect, so a small adjustment starts from the current colour
            // rather than from black.
            Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
            FullOpen = true,
            AnyColor = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        onPicked(RealtimeSubtitleColors.Format(
            System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
    }

    private void Persist(Action<AppSettings> apply)
    {
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        RenderColours();
    }

    /// <summary>
    /// Paints the two swatches, their hex labels, the opacity reading and the preview.
    /// </summary>
    /// <remarks>
    /// The colours come from what is stored; the opacity comes from the slider, which during a drag
    /// is deliberately ahead of it — see <see cref="ScrimOpacitySlider_ValueChanged"/>.
    /// </remarks>
    private void RenderColours()
    {
        var settings = SettingsService.Instance.Current;
        var opacity = CurrentScrimOpacity;
        var text = RealtimeSubtitleColors.Text(settings.RealtimeTextColor);
        var scrim = RealtimeSubtitleColors.Scrim(settings.RealtimeScrimColor, opacity);

        ScrimOpacityValue.Text = $"{opacity}%";

        // The swatch shows the colour the user picked, so it is drawn opaque even for the scrim —
        // the preview below is where its translucency is on display.
        TextColorSwatch.Background = new SolidColorBrush(text);
        ScrimColorSwatch.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(scrim.R, scrim.G, scrim.B));

        TextColorValue.Text = RealtimeSubtitleColors.Format(text);
        ScrimColorValue.Text = RealtimeSubtitleColors.Format(scrim);

        PreviewText.Foreground = new SolidColorBrush(text);
        PreviewScrim.Background = new SolidColorBrush(scrim);

        ResetColorsBtn.IsEnabled =
            settings.RealtimeTextColor != RealtimeSubtitleColors.DefaultText ||
            settings.RealtimeScrimColor != RealtimeSubtitleColors.DefaultScrim ||
            opacity != RealtimeSubtitleColors.DefaultScrimOpacity;
    }

    private void BlockCountDown_Click(object sender, RoutedEventArgs e) => StepBlockCount(-1);

    private void BlockCountUp_Click(object sender, RoutedEventArgs e) => StepBlockCount(+1);

    private void StepBlockCount(int delta)
    {
        var next = Math.Clamp(_blockCount + delta, MinBlocks, MaxBlocks);
        if (next == _blockCount) return;

        _blockCount = next;

        // Kept, unlike the rest of a sitting: the shortcut has to offer the same number of blocks as
        // this stepper and cannot read it off a page that is not open. See AppSettings.
        SaveTranslationPreference(settings => settings.RealtimeBlockCount = next);

        // The blocks kept from the last sitting were drawn to fill a different count, so they are
        // no longer an answer to the question being asked. Dropped as the count moves rather than
        // checked when they are offered back, so the user sees the rule they were given.
        RealtimeSessionController.Instance.ForgetBlocks();

        RenderBlockCount();
    }

    /// <summary>
    /// Shows the count and switches off whichever button would do nothing.
    /// </summary>
    /// <remarks>
    /// Disabled rather than silently ignored at the ends: a button that still looks pressable and
    /// then does nothing reads as the application having missed the click.
    /// </remarks>
    private void RenderBlockCount()
    {
        var active = RealtimeSessionController.Instance.IsActive;

        BlockCountText.Text = _blockCount.ToString();
        BlockCountDown.IsEnabled = !active && _blockCount > MinBlocks;
        BlockCountUp.IsEnabled = !active && _blockCount < MaxBlocks;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RenderState);

    private void RenderState()
    {
        bool active = RealtimeSessionController.Instance.IsActive;

        PrimaryBtn.Content = LocalizationService.Get(
            active ? "S.Realtime.StopSession" : "S.Realtime.SelectBlocks");

        // Locked rather than hidden while running: the user can see what the session was started
        // with, and changing any of it would mean rebuilding every block anyway.
        SrcLangBox.IsEnabled = !active;
        TgtLangBox.IsEnabled = !active;
        ProviderBox.IsEnabled = !active;
        ScreenBox.IsEnabled = !active;
        RenderBlockCount();

        // Nothing can start without a source language, so the button says so by being unavailable
        // rather than by refusing after the fact, and the field keeps its prompt until answered.
        var hasSource = SrcLangBox.SelectedValue is string;
        PrimaryBtn.IsEnabled = active || hasSource;
        SrcLangPlaceholder.Visibility = hasSource ? Visibility.Collapsed : Visibility.Visible;

        SetStatus(
            active ? LocalizationService.Get("S.Realtime.ActiveHint") : "",
            isError: false);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }
}
