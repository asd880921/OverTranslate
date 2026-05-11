using System.Windows;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate;

public partial class TranslationWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly TranslationService _translationService = new();
    private readonly TtsService _tts = new();

    public TranslationWindow(string sourceText, string translatedText, string sourceLang, string targetLang)
    {
        InitializeComponent();
        Icon = AppIconService.CreateWindowIcon();
        InitializeSelectors(sourceLang, targetLang);

        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;

        // Attach after initial values are set so initialization doesn't trigger a save
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
    }

    private void SrcLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
    }

    private void TgtLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        SaveProviderSelection(provider);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowOrActivate(this);

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var srcVal = SrcLangBox.SelectedValue as string;
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Swap texts
        (SourceTextBox.Text, TranslatedTextBox.Text) = (TranslatedTextBox.Text, SourceTextBox.Text);

        // Swap target → source using explicit language mapping
        if (tgtVal != null)
        {
            var sourceCode = LanguageData.MapTargetToSourceCode(tgtVal);
            SrcLangBox.SelectedValue = sourceCode;
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Swap source → target using explicit language mapping
        if (srcVal != null)
        {
            var targetCode = LanguageData.MapSourceToTargetCode(srcVal);
            TgtLangBox.SelectedValue = targetCode;
        }
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;
    }

    private async void TranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = SettingsService.Instance.Current.ApiKey;
        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("缺少 API Key — 請先在設定中輸入", isError: true);
            return;
        }

        var srcLang = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        var tgtLang = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        var text    = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        SetStatus("翻譯中...", isError: false);

        try
        {
            var block = new OcrTextBlock(text, new System.Windows.Rect());
            var (results, _) = await _translationService.TranslateAsync([block], srcLang, tgtLang, apiKey);
            TranslatedTextBox.Text = results.FirstOrDefault()?.TranslatedText ?? "";
            SetStatus("", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"翻譯失敗：{ex.Message}", isError: true);
        }
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var lang = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus($"朗讀失敗：{ex.Message}", isError: true); }
    }

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslatedTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var lang = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus($"朗讀失敗：{ex.Message}", isError: true); }
    }

    public void SetContent(string sourceText, string translatedText, string srcLang, string tgtLang)
    {
        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;
        StatusText.Text        = "";

        SrcLangBox.SelectedValue = LanguageData.GetValidSourceCode(srcLang);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(tgtLang);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tts.Dispose();
        base.OnClosed(e);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text       = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }

    private void InitializeSelectors(string sourceLang, string targetLang)
    {
        SrcLangBox.ItemsSource  = LanguageData.SourceLanguages;
        TgtLangBox.ItemsSource  = LanguageData.TargetLanguages;
        ProviderBox.ItemsSource = LanguageData.Providers;

        SrcLangBox.SelectedValue  = LanguageData.GetValidSourceCode(sourceLang);
        TgtLangBox.SelectedValue  = LanguageData.GetValidTargetCode(targetLang);
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
    }

    private void SaveCurrentLanguageSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        settings.TargetLanguage = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        SettingsService.Instance.Save();
    }

    private static void SaveProviderSelection(TranslationProvider provider)
    {
        var settings = SettingsService.Instance.Current;
        settings.Provider = provider;
        SettingsService.Instance.Save();
    }
}
