using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views;
using OverTranslate.Views.Controls;
using Microsoft.Win32;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Settings persist the moment a control changes — there is no save button, so every handler
/// routes through <see cref="Persist"/>, which is inert while <see cref="_loading"/> is set.
/// </summary>
/// <remarks>
/// What a single translation service has to be told is not on this page: DeepL's key and OpenAI's
/// endpoint, model and prompt live in <see cref="ServiceSettingsOverlay"/>, reached from the tile
/// for that service. Everything here applies whichever service is chosen.
/// </remarks>
public partial class SettingsPage : UserControl
{
    private readonly DispatcherTimer _statusHold;

    /// <summary>
    /// The bundle written by the last press, so the row that appears afterwards can open the
    /// thing that was just sent. Null until the first export of this session.
    /// </summary>
    private string? _lastBundlePath;
    private readonly DispatcherTimer _hotkeyGamepadRecordTimer;
    private readonly ushort[] _recordGamepadButtons = new ushort[4];

    /// <summary>
    /// One editable shortcut: the controls that edit it and the settings it reads and writes.
    /// </summary>
    /// <param name="Action">
    /// Which shortcut this is, so the row can be matched against <see cref="HotkeyBindings.Resolve"/>
    /// — the one place that decides which of two shortcuts sharing a combination stays on.
    /// </param>
    /// <param name="AdvertisedInShell">
    /// Whether the shell's nav rail prints this combination beside a 快速工具 row, and so has to be
    /// told when it changes. True for the two shortcuts those rows name; false for the rest, which
    /// the interface names nowhere.
    /// </param>
    /// <remarks>
    /// There is no record button in the record because there is none on the page: the box is
    /// read-only and starts recording when it is clicked, so a button beside it would have been a
    /// second way to do the one thing clicking the box already does.
    /// </remarks>
    /// <param name="EnabledBox">
    /// The tick that turns this shortcut off, or null for the capture one, which has no tick at all
    /// and no setting behind it because that shortcut is the feature the application is for. Null
    /// here is what says "this row cannot be turned off".
    /// </param>
    /// <param name="EnabledLabel">
    /// The word beside the switch saying which way it is set. Null for the capture row, which has
    /// no switch to describe.
    /// </param>
    /// <remarks>
    /// A row shadowed by a higher-priority shortcut says nothing about it. Priority still decides
    /// which of two shortcuts sharing a combination is registered — see
    /// <see cref="HotkeyBindings.Resolve"/>, and MainWindow logs the one it dropped — but the page
    /// does not carry a line for a state a user reaches only by having set the clash themselves.
    /// </remarks>
    private sealed record HotkeyField(
        HotkeyAction Action,
        string NameKey,
        TextBox Box,
        Func<AppSettings, string> Display,
        Func<AppSettings, ShortcutTrigger> Trigger,
        Action<AppSettings, ShortcutTrigger, string> Apply,
        bool AdvertisedInShell,
        // Fully qualified: WinForms is also referenced here and has its own CheckBox.
        System.Windows.Controls.CheckBox? EnabledBox = null,
        Action<AppSettings, bool>? SetEnabled = null,
        TextBlock? EnabledLabel = null);

    private HotkeyField[] _hotkeyFields = [];

    /// <summary>The field waiting for a key, or null. At most one at a time.</summary>
    private HotkeyField? _recording;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        // In the order the page lists them, which is not the order HotkeyBindings resolves them in:
        // there, position is priority and the newest shortcut goes last, while here it is what a
        // reader meets first. Nothing depends on this order — rows are looked up by action.
        _hotkeyFields =
        [
            new HotkeyField(
                HotkeyAction.Capture,
                "S.Settings.CaptureHotkey",
                HotkeyBox,
                s => s.HotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.Capture),
                ApplyCaptureTrigger,
                AdvertisedInShell: true),
            new HotkeyField(
                HotkeyAction.QuickLookup,
                "S.Settings.QuickLookupHotkey",
                QuickLookupHotkeyBox,
                s => s.QuickLookupHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.QuickLookup),
                ApplyQuickLookupTrigger,
                AdvertisedInShell: true,
                QuickLookupHotkeyEnabledCheckBox,
                (s, on) => s.QuickLookupHotkeyEnabled = on,
                QuickLookupHotkeyEnabledLabel),
            new HotkeyField(
                HotkeyAction.TranslationWindow,
                "S.Settings.WindowHotkey",
                WindowHotkeyBox,
                s => s.TranslationWindowHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.TranslationWindow),
                ApplyWindowTrigger,
                AdvertisedInShell: false,
                WindowHotkeyEnabledCheckBox,
                (s, on) => s.TranslationWindowHotkeyEnabled = on,
                WindowHotkeyEnabledLabel),
            new HotkeyField(
                HotkeyAction.RealtimePause,
                "S.Settings.RealtimePauseHotkey",
                RealtimePauseHotkeyBox,
                s => s.RealtimePauseHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.RealtimePause),
                ApplyRealtimePauseTrigger,
                AdvertisedInShell: false,
                RealtimePauseHotkeyEnabledCheckBox,
                (s, on) => s.RealtimePauseHotkeyEnabled = on,
                RealtimePauseHotkeyEnabledLabel),
        ];

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

        ApplyDiagnosticUploadAvailability();
        LoadSettings();
    }

    /// <summary>
    /// Re-reads the stored settings, so a change made elsewhere — a key typed into the service
    /// panel, a shortcut rebound — is on screen when this page is next shown.
    /// </summary>
    public void Reload() => LoadSettings();

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            foreach (var field in _hotkeyFields) field.Box.Text = field.Display(s);

            // The ticks, then the "a shortcut above took this" lines the ticks can change. The
            // availability pass is explicit because the toggle handler ignores changes made while
            // loading, so nothing else would grey these rows out on the way in.
            foreach (var binding in HotkeyBindings.Resolve(s))
                if (FieldFor(binding.Action)?.EnabledBox is { } box)
                    box.IsChecked = binding.Enabled;

            foreach (var field in _hotkeyFields) ApplyHotkeyRowAvailability(field);

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

            RefreshServiceTiles();
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

    private void FlashSaved() => FlashSuccess(LocalizationService.Get("S.Settings.Saved"));

    /// <summary>
    /// The same line the auto-save confirmation uses, for the handful of actions that finish with
    /// something to say other than 已儲存 — an export naming the file it wrote, so far.
    /// </summary>
    private void FlashSuccess(string message)
    {
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppSuccess");

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

    /// <summary>
    /// The status line for something that has started and has not finished. Neither colour fits:
    /// green would be a claim, red an accusation, and the fade the success path uses would take the
    /// line away while the thing it describes is still running — so this one holds until whoever
    /// started it replaces it.
    /// </summary>
    private void ShowProgress(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppTextSecondary");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    private void ShowError(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppError");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    // ── Field handlers ───────────────────────────────────────────────────────

    // ── Translation services ─────────────────────────────────────────────────

    /// <summary>Which service a tile's button configures.</summary>
    private TranslationProvider? ProviderOf(object sender) => sender switch
    {
        _ when ReferenceEquals(sender, DeepLConfigureBtn)  => TranslationProvider.DeepL,
        _ when ReferenceEquals(sender, OpenAiConfigureBtn) => TranslationProvider.OpenAI,
        _ => null,
    };

    private void ConfigureService_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderOf(sender) is not { } provider) return;

        // The panel belongs to the shell rather than to this page so that it covers the nav rail
        // too: a modal the user can navigate out from behind is not one.
        if (Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.OpenServiceSettings(provider);
    }

    /// <summary>
    /// Brings the two tiles in line with what is stored: what each service still needs before it
    /// can translate.
    /// </summary>
    /// <remarks>
    /// Public because the shell calls it when the settings panel is dismissed: what was typed in
    /// there is exactly what these tiles report.
    /// </remarks>
    public void RefreshServiceTiles()
    {
        var s = SettingsService.Instance.Current;

        // DeepL cannot translate a word without a key, so an empty one is something still to do.
        WriteServiceBadge(DeepLBadge, DeepLBadgeText, configured: s.ApiKey.Trim().Length > 0, required: true);

        // OpenAI has a working endpoint, model and prompt built in — it is aimed at a local model —
        // so nothing here is missing, only either left as shipped or changed.
        var openAiTouched =
            s.OpenAiBaseUrl.Trim().Length > 0 ||
            s.OpenAiApiKey.Trim().Length > 0 ||
            s.OpenAiModel.Trim().Length > 0;
        WriteServiceBadge(OpenAiBadge, OpenAiBadgeText, configured: openAiTouched, required: false);
    }

    /// <param name="required">
    /// Whether the service refuses to run until it is configured. Only that case is worth a warning
    /// colour; a service that works as shipped is reporting a choice, not a gap.
    /// </param>
    private void WriteServiceBadge(Border badge, TextBlock text, bool configured, bool required)
    {
        var key = configured
            ? "S.Settings.ServiceReady"
            : required ? "S.Settings.ServiceNeedsSetup" : "S.Settings.ServiceDefault";

        text.Text = LocalizationService.Get(key);

        // SetResourceReference rather than a resolved brush: FindResource hands back the brush the
        // theme in force at the time holds, and a tile written once would then keep the dark
        // theme's fill after the user switched to the light one.
        var warn = !configured && required;
        badge.SetResourceReference(Border.BackgroundProperty, warn ? "AppWarningSubtle" : "AppAccentSubtle");
        text.SetResourceReference(TextBlock.ForegroundProperty, warn ? "AppWarning" : "AppAccent");
    }

    // ── General ──────────────────────────────────────────────────────────────

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
    /// Four things on this page are composed in code and so hold a string from the language that
    /// was in effect when they were built: the provider list and its hint, the pickers' language
    /// labels, the service tiles' state words, and the environment-override notice. LoadSettings
    /// rebuilds all of them, and is already guarded against writing back.
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

    private void OpenLogFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticBundleService.OpenLogFolder();
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    /// <summary>
    /// Collects the bundle and, where an endpoint is compiled in, sends it and shows the code that
    /// comes back.
    /// </summary>
    /// <remarks>
    /// One button for both halves, because the two halves are one intention: nobody collects a
    /// diagnostic bundle for its own sake. What that costs is the chance to open the zip before it
    /// goes — paid for by the explanation on the heading, by a label that says it uploads, and by
    /// the row afterwards that opens what was sent.
    ///
    /// The upload is nested inside its own try on purpose. A failure there is not a failure of the
    /// press: the bundle is already written, and every one of those paths ends by opening Explorer
    /// on it — which is the whole of #126, still there for the offline machine, the blocked network
    /// and the person who would simply rather attach it themselves.
    ///
    /// Off the UI thread because the bundle copies and compresses every log file there is, which on
    /// a machine that has filled its five archives is a dozen megabytes — not long, but long enough
    /// to freeze the window on a slow disk, and freezing while collecting a bug report is its own
    /// bug report. The button is disabled meanwhile so the same zip cannot be started twice.
    /// </remarks>
    private async void ExportDiagnosticsBtn_Click(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsBtn.IsEnabled = false;

        // A code from an earlier press describes an earlier upload. Leaving it on screen through
        // the next one invites it to be copied as though it were the new one.
        DiagnosticsCodePanel.Visibility = Visibility.Collapsed;

        var uploading = DiagnosticUploadService.IsConfigured;
        try
        {
            if (uploading) ShowProgress(LocalizationService.Get("S.Settings.DiagnosticsUploading"));

            var path = await Task.Run(() => DiagnosticBundleService.Export());
            _lastBundlePath = path;

            if (!uploading)
            {
                FlashSuccess(LocalizationService.Get("S.Settings.DiagnosticsExported"));

                // The real confirmation: the status line fades after a moment and cannot show a path
                // worth reading anyway, whereas Explorer opens with the file already selected and
                // ready to be dragged into a forum post — which is the entire point of the feature.
                DiagnosticBundleService.Reveal(path);
                return;
            }

            try
            {
                var code = await DiagnosticUploadService.UploadAsync(path);

                DiagnosticsCodeText.Text = code;
                DiagnosticsCodePanel.Visibility = Visibility.Visible;
                FlashSuccess(LocalizationService.Format("S.Settings.DiagnosticsUploaded", code));
            }
            catch (DiagnosticUploadException ex)
            {
                ShowError(LocalizationService.Get(FailureMessageKey(ex.Reason)));
                DiagnosticBundleService.Reveal(path);
            }
        }
        catch (Exception ex)
        {
            // Only the collection can land here now, and if that failed there is no file to fall
            // back to — which is why this one still names the error.
            ShowError(LocalizationService.Format("S.Settings.DiagnosticsFailed", ex.Message));
        }
        finally
        {
            ExportDiagnosticsBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Every one of these lines ends by pointing at the file still on the user's disk, because that
    /// is what they are to do next in every case.
    /// </summary>
    private static string FailureMessageKey(DiagnosticUploadFailure reason) => reason switch
    {
        DiagnosticUploadFailure.Unreachable => "S.Settings.UploadFailedUnreachable",
        DiagnosticUploadFailure.TooLarge    => "S.Settings.UploadFailedTooLarge",
        DiagnosticUploadFailure.RateLimited => "S.Settings.UploadFailedRateLimited",
        _                                   => "S.Settings.UploadFailedRejected",
    };

    /// <summary>
    /// Points the export button and its explanation at whichever of the two stories is true for this
    /// build: with an endpoint compiled in, one press collects and sends; without one, it collects
    /// and nothing leaves the machine.
    /// </summary>
    /// <remarks>
    /// Resource references rather than assignments, so both survive a language change — the page
    /// rebuilds itself on one, and a plain Text assignment would come back in the old language.
    ///
    /// A build with no endpoint is a supported state, not a broken one: it is what every build is
    /// until the worker is deployed, and what a user gets by setting OVERTRANSLATE_DIAG_ENDPOINT to
    /// nothing.
    /// </remarks>
    private void ApplyDiagnosticUploadAvailability()
    {
        var configured = DiagnosticUploadService.IsConfigured;

        ExportDiagnosticsLabel.SetResourceReference(
            TextBlock.TextProperty,
            configured ? "S.Settings.UploadDiagnostics" : "S.Settings.ExportDiagnostics");

        DiagnosticsHintText.SetResourceReference(
            TextBlock.TextProperty,
            configured ? "S.Settings.DiagnosticsUploadHint" : "S.Settings.DiagnosticsHint");
    }

    /// <remarks>
    /// The clipboard is a shared, lockable resource, and the one thing that must not happen is the
    /// user walking away believing they have the code. On failure the code stays on screen and the
    /// line says to read it from there.
    /// </remarks>
    private void CopyDiagnosticsCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = DiagnosticsCodeText.Text;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            Clipboard.SetText(code);
            FlashSuccess(LocalizationService.Get("S.Settings.DiagnosticsCodeCopied"));
        }
        catch (Exception)
        {
            ShowError(LocalizationService.Get("S.Settings.CopyFailed"));
        }
    }

    /// <remarks>
    /// Opens the zip rather than selecting it, because the question this button answers is "what did
    /// I just send" — and Explorer shows the three files inside a zip it opens.
    /// </remarks>
    private void OpenDiagnosticsBundleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBundlePath is null) return;

        try
        {
            DiagnosticBundleService.Open(_lastBundlePath);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    /// <summary>The field this box edits, or null for anything else.</summary>
    private HotkeyField? FieldOf(object sender) =>
        _hotkeyFields.FirstOrDefault(field => ReferenceEquals(field.Box, sender));

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

        // Written here rather than beside the switch, because this is the one place both the load
        // and the toggle already go through — and a switch whose word disagreed with it would be
        // worse than no word at all.
        if (field.EnabledLabel is { } label)
            label.Text = LocalizationService.Get(on ? "S.Settings.HotkeyOn" : "S.Settings.HotkeyOff");
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

        // A row the rail advertises drops its combination when it is switched off, so the rail is
        // as wrong after a tick as it is after a re-record.
        if (field.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHints();
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

    private static void ApplyRealtimePauseTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.RealtimePauseHotkeyInputKind = trigger.Kind;
        s.RealtimePauseHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.RealtimePauseHotkeyModifiers = trigger.Modifiers;
            s.RealtimePauseHotkeyVirtualKey = trigger.VirtualKey;
            s.RealtimePauseHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.RealtimePauseHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyQuickLookupTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.QuickLookupHotkeyInputKind = trigger.Kind;
        s.QuickLookupHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.QuickLookupHotkeyModifiers = trigger.Modifiers;
            s.QuickLookupHotkeyVirtualKey = trigger.VirtualKey;
            s.QuickLookupHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.QuickLookupHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private void StartRecording(HotkeyField field)
    {
        // Only one at a time: two boxes both asking to be pressed would leave the next key press
        // ambiguous to the user long before it was ambiguous to the code.
        StopRecording();

        _recording = field;
        field.Box.Text = LocalizationService.Get("S.Settings.HotkeyPromptAnyInput");
        field.Box.Focus();

        for (int i = 0; i < _recordGamepadButtons.Length; i++)
            _recordGamepadButtons[i] = GamepadInput.TryGetButtons(i, out var buttons) ? buttons : (ushort)0;
        _hotkeyGamepadRecordTimer.Start();
    }

    /// <summary>Ends recording and puts the stored trigger back in the box.</summary>
    /// <remarks>
    /// Reads the setting rather than remembering what was there, so this also serves as the way
    /// back after a successful capture: the new value has been persisted by then, and restoring
    /// from the settings shows it.
    /// </remarks>
    private void StopRecording()
    {
        _hotkeyGamepadRecordTimer.Stop();
        if (_recording is not { } field) return;

        field.Box.Text = field.Display(SettingsService.Instance.Current);
        _recording = null;

        // The box asks to be pressed by being focused, so it has to stop being focused once it has
        // been: a box left carrying the focus ring after the shortcut is taken reads as still
        // waiting for one. Only when the focus is still here — the path in from LostFocus runs
        // after the user has already put it somewhere else, and clearing then would take it back.
        if (field.Box.IsKeyboardFocused) Keyboard.ClearFocus();
    }

    /// <summary>
    /// Every click on a focused box toggles recording — start, then stop, then start again.
    /// </summary>
    /// <remarks>
    /// GotFocus can only answer the first click, because the box stays focused after it. Without
    /// this the box would record once and then ignore every further click until focus had left and
    /// come back, which is the state a user lands in the moment they change their mind.
    ///
    /// The focusing click is left alone so it is not handled twice: GotFocus starts the recording
    /// that click asked for.
    /// </remarks>
    private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FieldOf(sender) is not { } field || !field.Box.IsFocused) return;

        e.Handled = true;
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
        // press a mouse button anywhere on the page after starting to record instead of having to
        // aim at the read-only box itself.
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

        var trigger = ShortcutTrigger.Keyboard(mods, vk);

        // A bare key is not merely watched, it is taken from every other application — so only the
        // keys that can afford to be taken are offered. See HotkeyBindings.IsBindable.
        if (!HotkeyBindings.IsBindable(trigger))
        {
            ShowError(LocalizationService.Get("S.Settings.HotkeyNeedsModifier"));
            StopRecording();
            return;
        }

        var prefix = GlobalHotkey.ModifiersToString(mods);
        var display = string.IsNullOrEmpty(prefix) ? key.ToString() : $"{prefix}+{key}";
        CommitShortcut(recording, trigger, display);
    }

    /// <remarks>
    /// The left and right buttons are not offered, and cannot be: left is how the box is clicked to
    /// start recording in the first place, and a shortcut on either would fire on every click the
    /// user makes anywhere. That leaves middle and the two side buttons — matching what
    /// <see cref="GlobalAuxiliaryHotkeys"/> watches for.
    ///
    /// Handled is set so the press is not also acted on as a press: XButton1 and XButton2 are the
    /// browser's Back and Forward, which WPF routes to navigation.
    /// </remarks>
    private void SettingsPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_recording is not { } recording) return;

        var kind = e.ChangedButton switch
        {
            MouseButton.Middle => ShortcutInputKind.MouseMiddle,
            MouseButton.XButton1 => ShortcutInputKind.MouseX1,
            MouseButton.XButton2 => ShortcutInputKind.MouseX2,
            _ => ShortcutInputKind.Keyboard,
        };

        if (kind == ShortcutInputKind.Keyboard) return;

        e.Handled = true;
        CommitShortcut(
            recording,
            ShortcutTrigger.Mouse(kind),
            LocalizationService.Get(MouseButtonNameKey(kind)));
    }

    /// <summary>The string naming one mouse button in the shortcut box.</summary>
    private static string MouseButtonNameKey(ShortcutInputKind kind) => kind switch
    {
        ShortcutInputKind.MouseX1 => "S.Settings.MouseX1",
        ShortcutInputKind.MouseX2 => "S.Settings.MouseX2",
        _ => "S.Settings.MouseMiddle",
    };

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

    /// <remarks>
    /// Windows keys a registration by window and combination, so the second shortcut to claim one is
    /// simply refused — RegisterHotKey returns false and nothing else happens. Left to itself that
    /// reads as a shortcut that stopped working for no reason, so the clash is refused here, where
    /// there is something to say about it. The mouse and controller buttons are not registered with
    /// Windows at all, but they go through the same refusal so that one button cannot silently do two
    /// different things.
    ///
    /// A shortcut that is switched off does not hold its trigger — same rule as
    /// <see cref="HotkeyBindings"/>, so what the page refuses and what actually gets registered
    /// agree.
    /// </remarks>
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

        // After the write, so the box picks the new trigger up out of the settings.
        StopRecording();

        // The global hook holds the old trigger until it is rebound.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // The nav rail advertises these beside 截圖翻譯 and 取詞翻譯 and is on screen right now, so it
        // has to be told; nothing else re-reads them until the shell is next shown or activated. The
        // other shortcuts are advertised nowhere, so there is nothing to refresh.
        if (recording.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHints();
    }
}
