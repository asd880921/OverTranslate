using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views;
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
    private readonly DispatcherTimer _statusHold;

    /// <summary>
    /// One editable shortcut: the two controls that edit it and the settings it reads and writes.
    /// </summary>
    /// <param name="AdvertisedInShell">
    /// Whether the shell's nav rail prints this combination beside a button, and so has to be told
    /// when it changes. True for the capture shortcut, which the interface names in three places;
    /// false for the window one, which it names nowhere.
    /// </param>
    private sealed record HotkeyField(
        string Name,
        TextBox Box,
        Button Record,
        Func<AppSettings, string> Display,
        Func<AppSettings, (uint Modifiers, uint Key)> Combination,
        Action<AppSettings, uint, uint, string> Apply,
        bool AdvertisedInShell);

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
                "截圖翻譯",
                HotkeyBox, RecordBtn,
                s => s.HotkeyDisplay,
                s => (s.HotkeyModifiers, s.HotkeyVirtualKey),
                (s, mods, vk, display) =>
                {
                    s.HotkeyModifiers = mods;
                    s.HotkeyVirtualKey = vk;
                    s.HotkeyDisplay = display;
                },
                AdvertisedInShell: true),
            new HotkeyField(
                "開啟翻譯視窗",
                WindowHotkeyBox, WindowRecordBtn,
                s => s.TranslationWindowHotkeyDisplay,
                s => (s.TranslationWindowHotkeyModifiers, s.TranslationWindowHotkeyVirtualKey),
                (s, mods, vk, display) =>
                {
                    s.TranslationWindowHotkeyModifiers = mods;
                    s.TranslationWindowHotkeyVirtualKey = vk;
                    s.TranslationWindowHotkeyDisplay = display;
                },
                AdvertisedInShell: false),
        ];

        _apiKeyDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _apiKeyDebounce.Tick += (_, _) =>
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Text.Trim());
        };

        _openAiSettingsDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _openAiSettingsDebounce.Tick += (_, _) =>
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Text.Trim();
                s.OpenAiModel = OpenAiModelBox.Text.Trim();
            });
        };

        _statusHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _statusHold.Tick += (_, _) => { _statusHold.Stop(); FadeStatusOut(); };

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

            SourceLangBox.ItemsSource = LanguageData.OcrSourceLanguages;
            SourceLangBox.SelectedValue = LanguageData.GetValidOcrSourceCode(s.SourceLanguage);
            if (SourceLangBox.SelectedValue == null) SourceLangBox.SelectedIndex = 0;

            ProviderBox.ItemsSource = LanguageData.Providers;
            ProviderBox.SelectedValue = s.Provider;
            if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
            ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";

            foreach (var field in _hotkeyFields) field.Box.Text = field.Display(s);
            ApiKeyBox.Text = s.ApiKey;
            OpenAiBaseUrlBox.Text = s.OpenAiBaseUrl;
            OpenAiApiKeyBox.Text = s.OpenAiApiKey;
            OpenAiModelBox.Text = s.OpenAiModel;

            LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
            DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;

            StartupCheckBox.IsChecked = StartupService.IsEnabled;

            AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;

            SaveScreenshotCheckBox.IsChecked = s.SaveScreenshotToDisk;
            ScreenshotPathBox.Text = ScreenshotSaveService.ResolveDirectory(s.ScreenshotSavePath);

            VerboseLoggingCheckBox.IsChecked = s.VerboseLogging;

            UpdateApiKeyVisibility();
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
        StatusText.Text       = "✓ 已儲存";
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

    private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _apiKeyDebounce.Stop();
        _apiKeyDebounce.Start();
    }

    private void OpenAiSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
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
            ShowError($"✗ 無法設定開機啟動：{ex.Message}");
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
        VerboseLoggingHint.Text = "目前記錄等級由環境變數 OVERTRANSLATE_LOGLEVEL 指定，此選項暫時無效。";
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = DarkThemeRadio.IsChecked == true ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(theme);
        Persist(s => s.Theme = theme);
    }

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
            Title = "選擇截圖儲存資料夾",
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
            ShowError($"✗ 無法開啟資料夾：{ex.Message}");
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

    private void StartRecording(HotkeyField field)
    {
        // Only one at a time: two boxes both saying 請按下快捷鍵 would leave the next key press
        // ambiguous to the user long before it was ambiguous to the code.
        StopRecording();

        _recording = field;
        field.Box.Text = "請按下快捷鍵...";
        field.Record.Content = "取消";
        field.Box.Focus();
    }

    /// <summary>Ends recording and puts the stored combination back in the box.</summary>
    /// <remarks>
    /// Reads the setting rather than remembering what was there, so this also serves as the way
    /// back after a successful capture: the new value has been persisted by then, and restoring
    /// from the settings shows it.
    /// </remarks>
    private void StopRecording()
    {
        if (_recording is not { } field) return;

        field.Box.Text = field.Display(SettingsService.Instance.Current);
        field.Record.Content = "錄製";
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

        // 焦點移到該欄位的錄製鈕時由 Click 事件處理，這裡不介入
        if (Keyboard.FocusedElement == field.Record) return;

        StopRecording();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } recording || !ReferenceEquals(recording.Box, sender)) return;
        e.Handled = true;

        bool isSystemKey = e.Key == Key.System;
        var key = isSystemKey ? e.SystemKey : e.Key;

        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt  || key == Key.RightAlt  ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        uint mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl))  mods |= GlobalHotkey.MOD_CONTROL;
        if (isSystemKey || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mods |= GlobalHotkey.MOD_ALT;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods |= GlobalHotkey.MOD_SHIFT;

        if (mods == 0) return;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var display = $"{GlobalHotkey.ModifiersToString(mods)}+{key}";

        // Windows keys a registration by window and combination, so the second shortcut to claim
        // one is simply refused — RegisterHotKey returns false and nothing else happens. Left to
        // itself that reads as a shortcut that stopped working for no reason, so the clash is
        // refused here, where there is something to say about it.
        var settings = SettingsService.Instance.Current;
        var taken = _hotkeyFields.FirstOrDefault(
            field => !ReferenceEquals(field, recording) && field.Combination(settings) == (mods, vk));

        if (taken is not null)
        {
            ShowError($"✗ {display} 已指派給「{taken.Name}」");
            StopRecording();
            return;
        }

        Persist(s => recording.Apply(s, mods, vk, display));

        // After the write, so the box picks the new combination up out of the settings.
        StopRecording();

        // The global hook holds the old combination until it is rebound
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // The nav rail advertises the capture shortcut beside 截圖翻譯 and is on screen right now,
        // so it has to be told; nothing else re-reads it until the shell is next shown or
        // activated. The window shortcut is advertised nowhere, so there is nothing to refresh.
        if (recording.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHint();
    }
}
