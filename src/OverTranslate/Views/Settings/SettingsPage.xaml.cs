using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views;
using Microsoft.Win32;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

public partial class SettingsPage : UserControl
{
    private bool _isRecording;
    private uint _pendingModifiers;
    private uint _pendingVKey;
    private bool _themeRadioInit;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
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

        _themeRadioInit = true;
        LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
        DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;
        _themeRadioInit = false;

        StartupCheckBox.IsChecked = StartupService.IsEnabled;

        AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;

        SaveScreenshotCheckBox.IsChecked = s.SaveScreenshotToDisk;
        ScreenshotPathBox.Text = ScreenshotSaveService.ResolveDirectory(s.ScreenshotSavePath);

        UpdateApiKeyVisibility();
        UpdateScreenshotPathVisibility();
    }

    private void UpdateScreenshotPathVisibility()
    {
        ScreenshotPathRow.Visibility = SaveScreenshotCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SaveScreenshotCheckBox_Toggled(object sender, RoutedEventArgs e)
        => UpdateScreenshotPathVisibility();

    private void ScreenshotPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The box is read-only and acts as a button: clicking anywhere in it opens the folder picker.
        e.Handled = true;

        var dialog = new OpenFolderDialog
        {
            Title = "選擇截圖儲存資料夾",
            InitialDirectory = ScreenshotPathBox.Text
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ScreenshotPathBox.Text = dialog.FolderName;
    }

    private void OpenScreenshotFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScreenshotSaveService.OpenFolder(ScreenshotPathBox.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text       = $"✗ 無法開啟資料夾：{ex.Message}";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppError");
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

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateApiKeyVisibility();
        ProviderHint.Text = (ProviderBox.SelectedItem as ProviderItem)?.Hint ?? "";
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
        s.SourceLanguage   = LanguageData.GetValidOcrSourceCode(SourceLangBox.SelectedValue as string);
        s.Provider         = ProviderBox.SelectedValue is TranslationProvider p ? p : TranslationProvider.Microsoft;
        s.ApiKey           = ApiKeyBox.Text.Trim();
        s.AutoTranslateAfterSelection = AutoTranslateCheckBox.IsChecked == true;
        s.SaveScreenshotToDisk = SaveScreenshotCheckBox.IsChecked == true;
        // Store "" when the folder matches the default, so the setting follows the system
        // Pictures folder instead of freezing today's expanded path.
        var chosenPath = ScreenshotPathBox.Text.Trim();
        s.ScreenshotSavePath = string.Equals(
            chosenPath, ScreenshotSaveService.DefaultDirectory, StringComparison.OrdinalIgnoreCase)
            ? ""
            : chosenPath;

        SettingsService.Instance.Save();
        StartupService.Set(StartupCheckBox.IsChecked == true);

        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        StatusText.Text       = "✓ 設定已儲存";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppSuccess");
    }
}
