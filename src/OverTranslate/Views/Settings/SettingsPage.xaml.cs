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
    private readonly DispatcherTimer _statusHold;

    private bool _isRecording;
    private uint _pendingModifiers;
    private uint _pendingVKey;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        _apiKeyDebounce = new DispatcherTimer { Interval = ApiKeyDebounce };
        _apiKeyDebounce.Tick += (_, _) =>
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Text.Trim());
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

            HotkeyBox.Text = s.HotkeyDisplay;
            ApiKeyBox.Text = s.ApiKey;

            _pendingModifiers = s.HotkeyModifiers;
            _pendingVKey      = s.HotkeyVirtualKey;

            LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
            DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;

            StartupCheckBox.IsChecked = StartupService.IsEnabled;

            AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;

            SaveScreenshotCheckBox.IsChecked = s.SaveScreenshotToDisk;
            ScreenshotPathBox.Text = ScreenshotSaveService.ResolveDirectory(s.ScreenshotSavePath);

            UpdateApiKeyVisibility();
            UpdateScreenshotPathVisibility();
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
        var vis = (ProviderBox.SelectedItem as ProviderItem)?.RequiresApiKey == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyLabel.Visibility = vis;
        ApiKeyBox.Visibility   = vis;
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = !_isRecording;
        if (_isRecording)
        {
            HotkeyBox.Text = "請按下快捷鍵...";
            RecordBtn.Content = "取消";
            HotkeyBox.Focus();
        }
        else
        {
            HotkeyBox.Text = SettingsService.Instance.Current.HotkeyDisplay;
            RecordBtn.Content = "錄製";
        }
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            _isRecording = true;
            HotkeyBox.Text = "請按下快捷鍵...";
            RecordBtn.Content = "取消";
        }
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isRecording) return;
        // 焦點移到 RecordBtn 時由 Click 事件處理，這裡不介入
        if (Keyboard.FocusedElement == RecordBtn) return;

        _isRecording = false;
        HotkeyBox.Text = SettingsService.Instance.Current.HotkeyDisplay;
        RecordBtn.Content = "錄製";
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecording) return;
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
        _pendingModifiers = mods;
        _pendingVKey      = vk;

        var display = $"{GlobalHotkey.ModifiersToString(mods)}+{key}";
        HotkeyBox.Text    = display;
        _isRecording      = false;
        RecordBtn.Content = "錄製";

        Persist(s =>
        {
            s.HotkeyModifiers  = _pendingModifiers;
            s.HotkeyVirtualKey = _pendingVKey;
            s.HotkeyDisplay    = display;
        });

        // The global hook holds the old combination until it is rebound
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // The nav rail advertises this shortcut beside 截圖翻譯 and is on screen right now, so it
        // has to be told; nothing else re-reads it until the shell is next shown or activated.
        if (Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHint();
    }
}
