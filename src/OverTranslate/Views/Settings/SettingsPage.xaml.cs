using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Views;
using OverTranslate.Views.Controls;
using Microsoft.Win32;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Settings persist the moment a control changes — there is no save button, so every handler
/// routes through <see cref="Persist"/>, which is inert while <see cref="_loading"/> is set.
/// </summary>
public partial class SettingsPage : UserControl
{
    // Typing shouldn't hit the disk on every keystroke; the key is written once typing pauses.
    private static readonly TimeSpan ApiKeyDebounce = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _apiKeyDebounce;
    private readonly DispatcherTimer _openAiSettingsDebounce;
    private readonly DispatcherTimer _promptDebounce;
    private readonly DispatcherTimer _statusHold;
    private readonly DispatcherTimer _hotkeyGamepadRecordTimer;
    private readonly ushort[] _recordGamepadButtons = new ushort[4];

    private const int PromptAutoSegment = 0;
    private const int PromptExplicitSegment = 1;

    /// <summary>How many lines of prompt the box accepts.</summary>
    private const int PromptMaxLines = 200;

    /// <summary>True while the box is being cut back to the line limit, so its own edit is ignored.</summary>
    private bool _trimmingPrompt;

    /// <summary>
    /// Which of the two prompts the editor is currently holding. Kept alongside the switch's own
    /// selection because a pending edit has to be written to the prompt it was typed into, even if
    /// the user has since switched to the other one.
    /// </summary>
    private int _promptSegment = PromptAutoSegment;

    /// <summary>
    /// One editable shortcut: the controls that edit it and the settings it reads and writes.
    /// </summary>
    /// <param name="Action">
    /// Which shortcut this is, so the row can be matched against <see cref="HotkeyBindings.Resolve"/>
    /// — the one place that decides which of two shortcuts sharing a combination stays on.
    /// </param>
    /// <param name="AdvertisedInShell">
    /// Whether the shell's nav rail prints this combination beside a button, and so has to be told
    /// when it changes. True for the capture shortcut, which the interface names in three places;
    /// false for the other shortcuts, which it names nowhere.
    /// </param>
    /// <param name="EnabledBox">
    /// The tick that turns this shortcut off, or null for the capture one, which has no tick at all
    /// and no setting behind it because that shortcut is the feature the application is for. Null
    /// here is what says "this row cannot be turned off".
    /// </param>
    /// <param name="ShadowHint">
    /// Where to say that a higher-priority shortcut has taken this combination. Null for capture,
    /// which is the highest priority and so can never be the one shadowed.
    /// </param>
    private sealed record HotkeyField(
        HotkeyAction Action,
        string NameKey,
        TextBox Box,
        Button Record,
        Func<AppSettings, string> Display,
        Func<AppSettings, ShortcutTrigger> Trigger,
        Action<AppSettings, ShortcutTrigger, string> Apply,
        bool AdvertisedInShell,
        // Fully qualified: WinForms is also referenced here and has its own CheckBox.
        System.Windows.Controls.CheckBox? EnabledBox = null,
        Action<AppSettings, bool>? SetEnabled = null,
        TextBlock? ShadowHint = null);

    private HotkeyField[] _hotkeyFields = [];

    /// <summary>The field waiting for a key, or null. At most one at a time.</summary>
    private HotkeyField? _recording;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        _hotkeyFields =
        [
            new HotkeyField(
                HotkeyAction.Capture,
                "S.Settings.CaptureHotkey",
                HotkeyBox, RecordBtn,
                s => s.HotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.Capture),
                ApplyCaptureTrigger,
                AdvertisedInShell: true),
            new HotkeyField(
                HotkeyAction.TranslationWindow,
                "S.Settings.WindowHotkey",
                WindowHotkeyBox, WindowRecordBtn,
                s => s.TranslationWindowHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.TranslationWindow),
                ApplyWindowTrigger,
                AdvertisedInShell: false,
                WindowHotkeyEnabledCheckBox,
                (s, on) => s.TranslationWindowHotkeyEnabled = on,
                WindowHotkeyShadowHint),
            new HotkeyField(
                HotkeyAction.Realtime,
                "S.Settings.RealtimeHotkey",
                RealtimeHotkeyBox, RealtimeRecordBtn,
                s => s.RealtimeHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.Realtime),
                ApplyRealtimeTrigger,
                AdvertisedInShell: false,
                RealtimeHotkeyEnabledCheckBox,
                (s, on) => s.RealtimeHotkeyEnabled = on,
                RealtimeHotkeyShadowHint),
            new HotkeyField(
                HotkeyAction.SingleShot,
                "S.Settings.SingleShotHotkey",
                SingleShotHotkeyBox, SingleShotRecordBtn,
                s => s.SingleShotHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.SingleShot),
                ApplySingleShotTrigger,
                AdvertisedInShell: false,
                SingleShotHotkeyEnabledCheckBox,
                (s, on) => s.SingleShotHotkeyEnabled = on,
                SingleShotHotkeyShadowHint),
        ];

        _apiKeyDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _apiKeyDebounce.Tick += (_, _) =>
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Secret.Trim());
        };

        _openAiSettingsDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _openAiSettingsDebounce.Tick += (_, _) =>
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Secret.Trim();
                s.OpenAiModel = OpenAiModelBox.Text.Trim();
                s.OpenAiTemperature = ReadTemperature();
            });
        };

        _promptDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _promptDebounce.Tick += (_, _) =>
        {
            _promptDebounce.Stop();
            PersistPrompt();
        };

        _hotkeyGamepadRecordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _hotkeyGamepadRecordTimer.Tick += (_, _) => PollRecordingGamepad();

        _statusHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _statusHold.Tick += (_, _) => { _statusHold.Stop(); FadeStatusOut(); };

        // Paired with Loaded rather than subscribed once: the shell keeps one instance of this
        // page for its lifetime and swaps it in and out of the content host, so unsubscribing on
        // Unloaded without re-subscribing on Loaded would leave it deaf from the first time the
        // user navigated away. A static event holding an instance handler also has to be let go
        // of at some point, which rules out subscribing only once.
        Loaded   += (_, _) => LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            StopRecording();
        };

        LoadSettings();
    }

    /// <summary>
    /// Re-reads the stored settings. The translation page writes the same language/provider
    /// fields, so navigating back here has to pick up whatever it changed.
    /// </summary>
    public void Reload() => LoadSettings();

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            LocalizationService.BindLocalizedItems(SourceLangBox, LanguageData.OcrSourceLanguages);
            SourceLangBox.SelectedValue = LanguageData.GetValidOcrSourceCode(s.SourceLanguage);
            if (SourceLangBox.SelectedValue == null) SourceLangBox.SelectedIndex = 0;

            LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);
            ProviderBox.SelectedValue = s.Provider;
            if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
            ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";

            foreach (var field in _hotkeyFields) field.Box.Text = field.Display(s);

            // The ticks, then the "a shortcut above took this" lines the ticks can change. The
            // availability pass is explicit because the toggle handler ignores changes made while
            // loading, so nothing else would grey these rows out on the way in.
            foreach (var binding in HotkeyBindings.Resolve(s))
                if (FieldFor(binding.Action)?.EnabledBox is { } box)
                    box.IsChecked = binding.Enabled;

            foreach (var field in _hotkeyFields) ApplyHotkeyRowAvailability(field);

            RefreshHotkeyShadowHints();
            ApiKeyBox.Secret = s.ApiKey;
            OpenAiBaseUrlBox.Text = s.OpenAiBaseUrl;
            OpenAiApiKeyBox.Secret = s.OpenAiApiKey;
            OpenAiModelBox.Text = s.OpenAiModel;
            TemperatureEnabledCheckBox.IsChecked = s.OpenAiTemperatureEnabled;
            TemperatureBox.Text = FormatTemperature(s.OpenAiTemperature);
            LoadPromptEditor(s);

            LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
            DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;

            // LocalizationService.Current, not s.UiLanguage: an unset preference is showing the
            // system default right now, and the picker has to agree with what is on screen.
            UiLanguageBox.ItemsSource = LocalizationService.Options;
            UiLanguageBox.SelectedValue = LocalizationService.Current;
            if (UiLanguageBox.SelectedValue == null) UiLanguageBox.SelectedIndex = 0;

            StartupCheckBox.IsChecked = StartupService.IsEnabled;

            AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;

            SaveScreenshotCheckBox.IsChecked = s.SaveScreenshotToDisk;
            ScreenshotPathBox.Text = ScreenshotSaveService.ResolveDirectory(s.ScreenshotSavePath);

            VerboseLoggingCheckBox.IsChecked = s.VerboseLogging;

            UpdateApiKeyVisibility();
            UpdateOpenAiFieldChrome();
            UpdateTemperatureChrome();
            UpdateScreenshotPathVisibility();
            UpdateVerboseLoggingAvailability();
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        FlashSaved();
    }

    private void FlashSaved()
    {
        StatusText.Text       = LocalizationService.Get("S.Settings.Saved");
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppSuccess");

        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1;
        AutoSaveHint.Opacity = 0;

        _statusHold.Stop();
        _statusHold.Start();
    }

    private void FadeStatusOut()
    {
        var fade = new DoubleAnimation
        {
            From = 1, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300))
        };
        fade.Completed += (_, _) => AutoSaveHint.Opacity = 1;
        StatusText.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowError(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppError");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    // ── Field handlers ───────────────────────────────────────────────────────

    private void SourceLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => Persist(s => s.SourceLanguage = LanguageData.GetValidOcrSourceCode(SourceLangBox.SelectedValue as string));

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateApiKeyVisibility();
        ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";
        Persist(s => s.Provider = ProviderBox.SelectedValue is TranslationProvider p
            ? p
            : TranslationProvider.Microsoft);
    }

    private void ApiKeyBox_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _apiKeyDebounce.Stop();
        _apiKeyDebounce.Start();
    }

    private void OpenAiSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Unconditionally: the placeholders answer what an empty box will do, and waiting for the
        // debounce would leave them a beat behind the typing.
        UpdateOpenAiFieldChrome();

        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    /// <summary>
    /// Shows what each empty box falls back to, in place of the empty box.
    /// </summary>
    private void UpdateOpenAiFieldChrome()
    {
        OpenAiBaseUrlPlaceholder.Text = OpenAiCompatibleProvider.DefaultBaseUrl;
        OpenAiBaseUrlPlaceholder.Visibility =
            OpenAiBaseUrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        OpenAiModelPlaceholder.Text = OpenAiCompatibleProvider.DefaultModel;
        OpenAiModelPlaceholder.Visibility =
            OpenAiModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAiSecret_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    // ── OpenAI advanced settings ─────────────────────────────────────────────

    /// <summary>How long the advanced section takes to open or close.</summary>
    private static readonly Duration AdvancedDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Widest temperature any of these APIs accepts; the field is clamped to it.</summary>
    private const double MaxTemperature = 2;

    private bool _openAiAdvancedExpanded;

    /// <summary>
    /// Which open/close is current, so a run that is replaced part way through does not then finish
    /// and hand the section a height belonging to the state it was leaving.
    /// </summary>
    private int _openAiAdvancedTransition;

    private void OpenAiAdvancedToggle_Click(object sender, RoutedEventArgs e) =>
        SetOpenAiAdvancedExpanded(!_openAiAdvancedExpanded);

    /// <remarks>
    /// The height is animated from the content's measured height rather than from a number written
    /// here, and handed back to Auto once open: the sentences inside are localized and wrap against
    /// the window's width, so today's measurement is not tomorrow's.
    /// </remarks>
    private void SetOpenAiAdvancedExpanded(bool expanded)
    {
        _openAiAdvancedExpanded = expanded;
        var transition = ++_openAiAdvancedTransition;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OpenAiAdvancedChevronRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(expanded ? 180 : 0, AdvancedDuration) { EasingFunction = ease });

        // Visible for the whole of the opening move, and only taken out of the layout once the
        // closing one has finished — collapsed, its content is out of the tab order as well as out
        // of sight, which a zero height alone would not manage.
        if (expanded) OpenAiAdvancedHost.Visibility = Visibility.Visible;

        var from = OpenAiAdvancedHost.ActualHeight;
        double to = 0;
        if (expanded)
        {
            var width = OpenAiAdvancedHost.ActualWidth;
            OpenAiAdvancedContent.Measure(new System.Windows.Size(
                width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
            to = OpenAiAdvancedContent.DesiredSize.Height;
        }

        var height = new DoubleAnimation(from, to, AdvancedDuration) { EasingFunction = ease };
        height.Completed += (_, _) =>
        {
            if (transition != _openAiAdvancedTransition) return;
            OpenAiAdvancedHost.BeginAnimation(HeightProperty, null);
            if (expanded)
            {
                OpenAiAdvancedHost.Height = double.NaN;
            }
            else
            {
                OpenAiAdvancedHost.Height = 0;
                OpenAiAdvancedHost.Visibility = Visibility.Collapsed;
            }
        };

        OpenAiAdvancedHost.BeginAnimation(HeightProperty, height);
        OpenAiAdvancedHost.BeginAnimation(
            OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, AdvancedDuration));
    }

    private void TemperatureEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateTemperatureChrome();
        if (_loading) return;
        Persist(s => s.OpenAiTemperatureEnabled = TemperatureEnabledCheckBox.IsChecked == true);
    }

    private void TemperatureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTemperatureChrome();

        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    /// <summary>Back to sending a temperature, and back to zero.</summary>
    private void TemperatureResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Assigned before the checkbox so its own handler, which reads the box, sees the new value.
        TemperatureBox.Text = FormatTemperature(0);
        TemperatureEnabledCheckBox.IsChecked = true;

        _openAiSettingsDebounce.Stop();
        Persist(s =>
        {
            s.OpenAiTemperatureEnabled = true;
            s.OpenAiTemperature = 0;
        });

        UpdateTemperatureChrome();
    }

    /// <summary>
    /// Puts the field back in agreement with what will actually be sent: an empty box, a number out
    /// of range, or something that is not a number at all all become the value stored for them.
    /// </summary>
    /// <remarks>
    /// On leaving the field rather than on each keystroke, so half-typed input is left alone —
    /// "0." is on the way to "0.5" and rewriting it mid-word would take the decimal point back out.
    /// </remarks>
    private void TemperatureBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = ReadTemperature();
        var text = FormatTemperature(value);
        if (TemperatureBox.Text != text) TemperatureBox.Text = text;

        _openAiSettingsDebounce.Stop();
        Persist(s => s.OpenAiTemperature = value);
    }

    /// <summary>The value in the box, or 0 for anything that is not a number in range.</summary>
    private double ReadTemperature()
    {
        var text = TemperatureBox.Text.Trim();

        // The invariant form first because that is what the field is written back as and what the
        // API takes; the user's own is tried after it, so a comma typed on a locale that uses one
        // is read as the decimal point it was meant to be.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return 0;

        return Math.Clamp(value, 0, MaxTemperature);
    }

    private static string FormatTemperature(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Brings the field and its reset in line with what is on screen: nothing to type into while the
    /// parameter is not being sent, and nothing to restore while both halves are already the default.
    /// </summary>
    private void UpdateTemperatureChrome()
    {
        var enabled = TemperatureEnabledCheckBox.IsChecked == true;
        TemperatureBox.IsEnabled = enabled;
        TemperatureRangeHint.Opacity = enabled ? 1 : 0.45;

        // The box rather than the stored value, so this answers on the first keystroke instead of
        // when the debounce eventually fires.
        TemperatureResetButton.IsEnabled = !enabled || TemperatureBox.Text.Trim() != FormatTemperature(0);
    }

    // ── Prompt editor ────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the switch and the editor. Also the language-change path, since the segment labels and
    /// the built-in wording shown behind an empty box are both localized.
    /// </summary>
    private void LoadPromptEditor(AppSettings s)
    {
        PromptSwitch.Items.Clear();
        PromptSwitch.Items.Add(new SegmentedItem(LocalizationService.Get("S.Settings.PromptAuto")));
        PromptSwitch.Items.Add(new SegmentedItem(LocalizationService.Get("S.Settings.PromptExplicit")));
        // Not animated: the user has not moved anything, the page is simply arriving.
        PromptSwitch.Select(_promptSegment, animate: false);

        PromptBox.Text = _promptSegment == PromptAutoSegment ? s.OpenAiPromptAuto : s.OpenAiPromptExplicit;
        UpdatePromptChrome();
    }

    private void PromptSwitch_SelectionChanged(object? sender, EventArgs e)
    {
        // The pending edit belongs to the prompt it was typed into, so it is written out before the
        // editor is handed to the other one.
        FlushPromptEdit();

        _promptSegment = PromptSwitch.SelectedIndex;

        var s = SettingsService.Instance.Current;
        var text = _promptSegment == PromptAutoSegment ? s.OpenAiPromptAuto : s.OpenAiPromptExplicit;

        // Assigning Text would raise TextChanged and start the debounce, which would then write
        // this prompt straight back over itself; _loading is what the rest of the page uses to mean
        // "this change came from us, not the user".
        _loading = true;
        try { PromptBox.Text = text; }
        finally { _loading = false; }

        UpdatePromptChrome();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The trim below raises this event again for its own edit.
        if (_trimmingPrompt) return;

        // Only what the user types or pastes is held to the limit. A longer prompt already in the
        // settings file is left alone until they touch it, rather than being quietly cut down on a
        // page they only came to look at.
        if (!_loading) TrimPromptToLineLimit();

        // Chrome unconditionally: the reset button describes what is in the box right now, and
        // waiting for the debounce would leave it a beat behind the typing.
        UpdatePromptChrome();

        if (_loading) return;
        _promptDebounce.Stop();
        _promptDebounce.Start();
    }

    /// <summary>
    /// Drops anything past <see cref="PromptMaxLines"/> lines, silently.
    /// </summary>
    /// <remarks>
    /// A cap on the input rather than a check further in: the prompt is sent once per recognised
    /// block, so a pasted document is a real cost repeated a dozen times over, and the place to
    /// stop it is where it arrives. Nothing is said about it — the box visibly refuses to grow,
    /// which is the whole message, and a warning about a limit nobody reaches by writing an
    /// instruction would only be in the way.
    ///
    /// Removed through the selection so the paste stays undoable; the trim is then simply applied
    /// again if the undone text is still too long.
    /// </remarks>
    private void TrimPromptToLineLimit()
    {
        var overflow = LineLimitOverflowIndex(PromptBox.Text, PromptMaxLines);
        if (overflow < 0) return;

        _trimmingPrompt = true;
        try
        {
            PromptBox.Select(overflow, PromptBox.Text.Length - overflow);
            PromptBox.SelectedText = "";
            PromptBox.CaretIndex = overflow;
        }
        finally
        {
            _trimmingPrompt = false;
        }
    }

    /// <summary>
    /// Where the text passes <paramref name="maxLines"/> lines, or -1 when it does not.
    /// </summary>
    /// <remarks>
    /// Hard line breaks only. <see cref="TextBox.LineCount"/> counts the lines actually drawn, so
    /// with wrapping on it would make the cap depend on how wide the window happens to be.
    /// </remarks>
    internal static int LineLimitOverflowIndex(string text, int maxLines)
    {
        var index = -1;
        for (var line = 0; line < maxLines; line++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0) return -1;
        }

        // Cut before the break that would have started the next line, and before the carriage
        // return in front of it, so the kept text does not end on a half of a CRLF pair.
        return index > 0 && text[index - 1] == '\r' ? index - 1 : index;
    }

    private void PromptResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Cleared through the selection rather than by assigning Text, which would throw away the
        // undo history: this discards something the user wrote, and Ctrl+Z getting it back is what
        // makes a confirmation prompt unnecessary.
        PromptBox.SelectAll();
        PromptBox.SelectedText = "";
        PromptBox.Focus();

        _promptDebounce.Stop();
        PersistPrompt();
    }

    private void FlushPromptEdit()
    {
        if (!_promptDebounce.IsEnabled) return;
        _promptDebounce.Stop();
        PersistPrompt();
    }

    private void PersistPrompt()
    {
        var text = PromptBox.Text.Trim();
        var segment = _promptSegment;

        Persist(s =>
        {
            if (segment == PromptAutoSegment) s.OpenAiPromptAuto = text;
            else s.OpenAiPromptExplicit = text;
        });

        UpdatePromptChrome();
    }

    /// <summary>
    /// Brings the reset button and the placeholder in line with what is on screen.
    /// </summary>
    private void UpdatePromptChrome()
    {
        if (PromptSwitch.Items.Count < 2) return;

        // Nothing to restore while the built-in one is in use, and a live button that does nothing
        // would be the page's only control that lies about having something to do. This reads the
        // box rather than the stored setting, so it answers on the first keystroke instead of when
        // the debounce eventually fires.
        PromptResetButton.IsEnabled = PromptBox.Text.Trim().Length > 0;

        PromptPlaceholder.Visibility = PromptBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WritePlaceholderAware(
            PromptPlaceholder,
            OpenAiCompatibleProvider.DefaultPromptTemplate(_promptSegment == PromptAutoSegment));

        // 自動 has no source language, so the two rows describing one would be listing parameters
        // that resolve to nothing. Hidden whole rather than left showing an empty example.
        var showsSource = _promptSegment != PromptAutoSegment;
        var sourceRows = showsSource ? Visibility.Visible : Visibility.Collapsed;
        ParamRowSourceName.Visibility = sourceRows;
        ParamRowSourceCode.Visibility = sourceRows;
    }

    /// <summary>
    /// Writes prose that mentions the prompt placeholders, with each picked out in the accent colour.
    /// </summary>
    /// <remarks>
    /// They are the only part of the sentence that is machinery rather than words — what the user
    /// types into their own prompt to have a language substituted in — and the colour is what says
    /// so without a sentence explaining it. The same colour the rest of the app uses for the thing
    /// being pointed at, through a resource reference so it follows a theme change.
    /// </remarks>
    private static void WritePlaceholderAware(TextBlock target, string text)
    {
        target.Inlines.Clear();
        foreach (var (segment, isPlaceholder) in SplitOnPlaceholders(text))
        {
            if (isPlaceholder)
            {
                var placeholder = new Run(segment);
                placeholder.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");
                target.Inlines.Add(placeholder);
                continue;
            }

            // The prose between placeholders may carry a line break — the explicit hint lists the
            // source pair and the target pair one per line. Added as a LineBreak rather than left in
            // a Run, so it does not depend on the block's wrapping to show up.
            var lines = segment.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0) target.Inlines.Add(new Run(lines[i]));
            }
        }
    }

    /// <summary>
    /// Splits text into runs of ordinary prose and the placeholder tokens between them, in order.
    /// </summary>
    private static IEnumerable<(string Text, bool IsPlaceholder)> SplitOnPlaceholders(string text)
    {
        // All four, or the tag placeholders would be the only machinery in the sentence left looking
        // like prose. None is a prefix of another once the closing brace is counted, so the
        // earliest-match loop below cannot pick the wrong one.
        string[] tokens =
        [
            OpenAiCompatibleProvider.SourcePlaceholder,
            OpenAiCompatibleProvider.TargetPlaceholder,
            OpenAiCompatibleProvider.SourceCodePlaceholder,
            OpenAiCompatibleProvider.TargetCodePlaceholder,
        ];

        var index = 0;
        while (index < text.Length)
        {
            var at = -1;
            var length = 0;
            foreach (var token in tokens)
            {
                var found = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0 || (at >= 0 && found >= at)) continue;
                at = found;
                length = token.Length;
            }

            if (at < 0)
            {
                yield return (text[index..], false);
                yield break;
            }

            if (at > index) yield return (text[index..at], false);
            yield return (text.Substring(at, length), true);
            index = at + length;
        }
    }

    private void OllamaGuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void AutoTranslate_Toggled(object sender, RoutedEventArgs e)
        => Persist(s => s.AutoTranslateAfterSelection = AutoTranslateCheckBox.IsChecked == true);

    // Startup lives in the registry rather than the settings file, so it saves on its own path
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            StartupService.Set(StartupCheckBox.IsChecked == true);
            FlashSaved();
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.StartupFailed", ex.Message));
        }
    }

    // Takes effect on the next line written, not on the next launch — the level is applied before it
    // is stored, so a user who is mid-reproduction can tick the box and keep going.
    private void VerboseLogging_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool verbose = VerboseLoggingCheckBox.IsChecked == true;
        LogLevelService.Apply(verbose);
        Persist(s => s.VerboseLogging = verbose);
    }

    /// <summary>
    /// An environment variable outranks this setting, so when one is set the checkbox says so rather
    /// than accepting clicks that change nothing.
    /// </summary>
    private void UpdateVerboseLoggingAvailability()
    {
        if (!LogLevelService.IsOverriddenByEnvironment) return;

        VerboseLoggingCheckBox.IsEnabled = false;
        VerboseLoggingHint.Text = LocalizationService.Get("S.Settings.LoggingEnvOverride");
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = DarkThemeRadio.IsChecked == true ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(theme);
        Persist(s => s.Theme = theme);
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (UiLanguageBox.SelectedValue as string is not { } language) return;

        // Persist first: the swap re-runs LoadSettings through LanguageChanged, and both that and
        // LocalizationService.Current read the stored value back.
        Persist(s => s.UiLanguage = language);
        LocalizationService.Apply(language);

        // Persist already flashed the confirmation, but it did so in the outgoing language and
        // LoadSettings does not touch the status line. Say it again in the new one.
        FlashSaved();
    }

    /// <summary>
    /// Re-renders the text on this page that DynamicResource cannot reach.
    /// </summary>
    /// <remarks>
    /// Three things on this page are composed in code and so hold a string from the language that
    /// was in effect when they were built: the provider list and its hint, the pickers' language
    /// labels, and the environment-override notice. LoadSettings rebuilds all of them, and is
    /// already guarded against writing back.
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs e) => LoadSettings();

    private void SaveScreenshotCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateScreenshotPathVisibility();
        Persist(s => s.SaveScreenshotToDisk = SaveScreenshotCheckBox.IsChecked == true);
    }

    private void UpdateScreenshotPathVisibility()
    {
        ScreenshotPathRow.Visibility = SaveScreenshotCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScreenshotPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The box is read-only and acts as a button: clicking anywhere in it opens the folder picker.
        e.Handled = true;

        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("S.Settings.FolderPickerTitle"),
            InitialDirectory = ScreenshotPathBox.Text
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ScreenshotPathBox.Text = dialog.FolderName;
        // Store "" when the folder matches the default, so the setting follows the system
        // Pictures folder instead of freezing today's expanded path.
        Persist(s => s.ScreenshotSavePath = string.Equals(
            dialog.FolderName, ScreenshotSaveService.DefaultDirectory, StringComparison.OrdinalIgnoreCase)
            ? ""
            : dialog.FolderName);
    }

    private void OpenScreenshotFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScreenshotSaveService.OpenFolder(ScreenshotPathBox.Text);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    private void UpdateApiKeyVisibility()
    {
        var provider = ProviderBox.SelectedValue as TranslationProvider?;
        DeepLApiKeyRow.Visibility = provider == TranslationProvider.DeepL
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenAiSettingsPanel.Visibility = provider == TranslationProvider.OpenAI
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    /// <summary>The field these two controls edit, or null for anything else.</summary>
    private HotkeyField? FieldOf(object sender) =>
        _hotkeyFields.FirstOrDefault(
            field => ReferenceEquals(field.Box, sender) || ReferenceEquals(field.Record, sender));

    /// <summary>The row that edits one action.</summary>
    private HotkeyField? FieldFor(HotkeyAction action) =>
        _hotkeyFields.FirstOrDefault(field => field.Action == action);

    /// <summary>
    /// Greys out the box and the record button of a shortcut that is switched off.
    /// </summary>
    /// <remarks>
    /// The tick is the whole row's switch, so leaving the rest of the row live invites the user to
    /// record a combination for a shortcut that will not be registered — an edit that appears to work
    /// and changes nothing.
    ///
    /// Only the tick does this. A row shadowed by a higher-priority shortcut stays editable on
    /// purpose: re-recording it onto a free combination is exactly how that is fixed, and disabling
    /// the row would take the fix away along with the problem.
    /// </remarks>
    private static void ApplyHotkeyRowAvailability(HotkeyField field)
    {
        // No tick means the row cannot be switched off at all — the capture shortcut.
        var on = field.EnabledBox is not { } box || box.IsChecked == true;

        field.Box.IsEnabled = on;
        field.Record.IsEnabled = on;
    }

    private void HotkeyEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var field = _hotkeyFields.FirstOrDefault(f => ReferenceEquals(f.EnabledBox, sender));
        if (field?.SetEnabled is not { } setEnabled || field.EnabledBox is not { } box) return;

        // Switching a row off while it is waiting for a key would leave it recording into a control
        // about to be disabled, and the tick would look like it had done nothing.
        if (ReferenceEquals(_recording, field)) StopRecording();

        Persist(s => setEnabled(s, box.IsChecked == true));
        ApplyHotkeyRowAvailability(field);

        // The global hooks are bound from these settings, so the tick means nothing until they are
        // rebound — without this the shortcut keeps working until the application is restarted, and
        // a switch that takes effect later is worse than no switch.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // Turning one off releases its combination, which can bring a shadowed row back — so the
        // hints below are re-read from the resolver rather than only cleared for this row.
        RefreshHotkeyShadowHints();
    }

    /// <summary>
    /// Says, per row, that a higher-priority shortcut holds this combination.
    /// </summary>
    /// <remarks>
    /// The recorder refuses to assign a combination another shortcut already holds, so this state
    /// cannot be reached by editing. It is reached by upgrading: adding the realtime shortcut gave
    /// every existing installation a Ctrl+Alt+S it never agreed to, and anyone who had already put
    /// that on the translation window would otherwise have had one of the two stop working with
    /// nothing said. Priority decides which, and this is where it is said out loud.
    /// </remarks>
    private void RefreshHotkeyShadowHints()
    {
        foreach (var binding in HotkeyBindings.Resolve(SettingsService.Instance.Current))
        {
            if (FieldFor(binding.Action)?.ShadowHint is not { } hint) continue;

            if (binding.ShadowedBy is not { } holder)
            {
                hint.Visibility = Visibility.Collapsed;
                continue;
            }

            var holderName = FieldFor(holder)?.NameKey;
            hint.Text = LocalizationService.Format(
                "S.Settings.HotkeyShadowed",
                holderName is null ? "" : LocalizationService.Get(holderName));
            hint.Visibility = Visibility.Visible;
        }
    }

    private static void ApplyCaptureTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.HotkeyInputKind = trigger.Kind;
        s.HotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.HotkeyModifiers = trigger.Modifiers;
            s.HotkeyVirtualKey = trigger.VirtualKey;
            s.HotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.HotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyWindowTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.TranslationWindowHotkeyInputKind = trigger.Kind;
        s.TranslationWindowHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.TranslationWindowHotkeyModifiers = trigger.Modifiers;
            s.TranslationWindowHotkeyVirtualKey = trigger.VirtualKey;
            s.TranslationWindowHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.TranslationWindowHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyRealtimeTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.RealtimeHotkeyInputKind = trigger.Kind;
        s.RealtimeHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.RealtimeHotkeyModifiers = trigger.Modifiers;
            s.RealtimeHotkeyVirtualKey = trigger.VirtualKey;
            s.RealtimeHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.RealtimeHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplySingleShotTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.SingleShotHotkeyInputKind = trigger.Kind;
        s.SingleShotHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.SingleShotHotkeyModifiers = trigger.Modifiers;
            s.SingleShotHotkeyVirtualKey = trigger.VirtualKey;
            s.SingleShotHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.SingleShotHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private void StartRecording(HotkeyField field)
    {
        StopRecording();

        _recording = field;
        field.Box.Text = LocalizationService.Get("S.Settings.HotkeyPromptAnyInput");
        field.Record.Content = LocalizationService.Get("S.Common.Cancel");
        field.Box.Focus();

        for (int i = 0; i < _recordGamepadButtons.Length; i++)
            _recordGamepadButtons[i] = GamepadInput.TryGetButtons(i, out var buttons) ? buttons : (ushort)0;
        _hotkeyGamepadRecordTimer.Start();
    }

    private void StopRecording()
    {
        _hotkeyGamepadRecordTimer.Stop();
        if (_recording is not { } field) return;

        field.Box.Text = field.Display(SettingsService.Instance.Current);
        field.Record.Content = LocalizationService.Get("S.Common.Record");
        _recording = null;
    }

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (FieldOf(sender) is not { } field) return;

        if (ReferenceEquals(_recording, field)) StopRecording();
        else StartRecording(field);
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (FieldOf(sender) is { } field && !ReferenceEquals(_recording, field))
            StartRecording(field);
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (FieldOf(sender) is not { } field || !ReferenceEquals(_recording, field)) return;

        // Keep recording while focus moves inside this settings page. This is what lets the user
        // press middle mouse anywhere on the page after clicking Record instead of having to aim at
        // the read-only box itself.
        if (Keyboard.FocusedElement is DependencyObject focused && IsDescendantOfThisPage(focused)) return;

        StopRecording();
    }

    private bool IsDescendantOfThisPage(DependencyObject child)
    {
        for (DependencyObject? current = child; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } recording || !ReferenceEquals(recording.Box, sender)) return;
        e.Handled = true;

        bool isSystemKey = e.Key == Key.System;
        var key = isSystemKey ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt  || key == Key.RightAlt  ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        uint mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl))  mods |= GlobalHotkey.MOD_CONTROL;
        if (isSystemKey || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mods |= GlobalHotkey.MOD_ALT;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods |= GlobalHotkey.MOD_SHIFT;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        var prefix = GlobalHotkey.ModifiersToString(mods);
        var display = string.IsNullOrEmpty(prefix) ? key.ToString() : $"{prefix}+{key}";
        CommitShortcut(recording, ShortcutTrigger.Keyboard(mods, vk), display);
    }

    private void SettingsPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_recording is not { } recording || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        CommitShortcut(
            recording,
            ShortcutTrigger.MouseMiddle(),
            LocalizationService.Get("S.Settings.MouseMiddle"));
    }

    private void PollRecordingGamepad()
    {
        if (_recording is not { } recording)
        {
            _hotkeyGamepadRecordTimer.Stop();
            return;
        }

        for (int i = 0; i < _recordGamepadButtons.Length; i++)
        {
            if (!GamepadInput.TryGetButtons(i, out var current))
            {
                _recordGamepadButtons[i] = 0;
                continue;
            }

            ushort pressed = (ushort)(current & ~_recordGamepadButtons[i]);
            _recordGamepadButtons[i] = current;
            var button = GamepadInput.FirstButton(pressed);
            if (button == GamepadShortcutButton.None) continue;

            var display = LocalizationService.Format(
                "S.Settings.GamepadButton",
                GamepadInput.ButtonName(button));
            CommitShortcut(recording, ShortcutTrigger.Gamepad(button), display);
            return;
        }
    }

    private void CommitShortcut(HotkeyField recording, ShortcutTrigger trigger, string display)
    {
        var settings = SettingsService.Instance.Current;
        var enabled = HotkeyBindings.Resolve(settings)
            .Where(binding => binding.Enabled)
            .Select(binding => binding.Action)
            .ToHashSet();

        var taken = _hotkeyFields.FirstOrDefault(
            field => !ReferenceEquals(field, recording) &&
                     enabled.Contains(field.Action) &&
                     field.Trigger(settings) == trigger);

        if (taken is not null)
        {
            ShowError(LocalizationService.Format(
                "S.Settings.HotkeyTaken", display, LocalizationService.Get(taken.NameKey)));
            StopRecording();
            return;
        }

        Persist(s => recording.Apply(s, trigger, display));
        StopRecording();
        RefreshHotkeyShadowHints();

        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        if (recording.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHint();
    }

}
