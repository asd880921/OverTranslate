using System.Windows;
using System.Windows.Input;
using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    public static void ShowOrActivate()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow();
        _instance.Show();
    }

    private bool _isRecording;
    private uint _pendingModifiers;
    private uint _pendingVKey;
    private bool _themeRadioInit;

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = AppIconService.CreateWindowIcon();
        LoadSettings();
        Closed += (_, _) => _instance = null;
    }

    private void LoadSettings()
    {
        var s = SettingsService.Instance.Current;

        SourceLangBox.ItemsSource = LanguageData.SourceLanguages;
        SourceLangBox.SelectedValue = s.SourceLanguage;
        if (SourceLangBox.SelectedValue == null) SourceLangBox.SelectedIndex = 0;

        OcrEngineBox.ItemsSource = LanguageData.OcrEngines;
        OcrEngineBox.SelectedValue = s.OcrEngine;
        if (OcrEngineBox.SelectedValue == null) OcrEngineBox.SelectedIndex = 0;
        OcrEngineHint.Text = (OcrEngineBox.SelectedItem as OcrEngineItem)?.Hint ?? "";

        ProviderBox.ItemsSource = LanguageData.Providers;
        ProviderBox.SelectedValue = s.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";

        HotkeyBox.Text = s.HotkeyDisplay;
        ApiKeyBox.Text = s.ApiKey;

        _pendingModifiers = s.HotkeyModifiers;
        _pendingVKey      = s.HotkeyVirtualKey;

        _themeRadioInit = true;
        LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
        DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;
        _themeRadioInit = false;

        UpdateApiKeyVisibility();
    }

    private void UpdateApiKeyVisibility()
    {
        var vis = (ProviderBox.SelectedItem as ProviderItem)?.RequiresApiKey == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyLabel.Visibility = vis;
        ApiKeyBox.Visibility   = vis;
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateApiKeyVisibility();
        ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";
    }

    private void OcrEngineBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        OcrEngineHint.Text = (OcrEngineBox.SelectedItem as OcrEngineItem)?.Hint ?? "";
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_themeRadioInit) return;
        var theme = DarkThemeRadio.IsChecked == true ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(theme);
        SettingsService.Instance.Current.Theme = theme;
        SettingsService.Instance.Save();
    }

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

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

        HotkeyBox.Text    = $"{GlobalHotkey.ModifiersToString(mods)}+{key}";
        _isRecording      = false;
        RecordBtn.Content = "錄製";
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance.Current;
        s.HotkeyModifiers  = _pendingModifiers;
        s.HotkeyVirtualKey = _pendingVKey;
        s.HotkeyDisplay    = HotkeyBox.Text;
        s.SourceLanguage   = (SourceLangBox.SelectedValue as string) ?? "auto";
        s.OcrEngine        = OcrEngineBox.SelectedValue is OcrEngineType eng ? eng : OcrEngineType.WindowsOcr;
        s.Provider         = ProviderBox.SelectedValue is TranslationProvider p ? p : TranslationProvider.Google;
        s.ApiKey           = ApiKeyBox.Text.Trim();

        SettingsService.Instance.Save();

        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        StatusText.Text       = "✓ 設定已儲存";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppSuccess");
    }
}
