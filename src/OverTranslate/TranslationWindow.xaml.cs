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

        SrcLangBox.ItemsSource  = LanguageData.SourceLanguages;
        TgtLangBox.ItemsSource  = LanguageData.TargetLanguages;
        ProviderBox.ItemsSource = LanguageData.Providers;

        SrcLangBox.SelectedValue  = sourceLang;
        TgtLangBox.SelectedValue  = targetLang;
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (SrcLangBox.SelectedValue  == null) SrcLangBox.SelectedIndex  = 0;
        if (TgtLangBox.SelectedValue  == null) TgtLangBox.SelectedIndex  = LanguageData.TargetLanguages.Count - 1;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;

        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;

        // Attach after initial values are set so initialization doesn't trigger a save
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
    }

    private void TgtLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var code = TgtLangBox.SelectedValue as string;
        if (string.IsNullOrEmpty(code)) return;
        var s = SettingsService.Instance.Current;
        s.TargetLanguage = code;
        SettingsService.Instance.Save();
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        var s = SettingsService.Instance.Current;
        s.Provider = provider;
        SettingsService.Instance.Save();
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowOrActivate();

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var srcVal = SrcLangBox.SelectedValue as string;
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Swap texts
        (SourceTextBox.Text, TranslatedTextBox.Text) = (TranslatedTextBox.Text, SourceTextBox.Text);

        // Swap target → source (strip region variant: EN-GB → EN)
        if (tgtVal != null)
        {
            var baseCode = tgtVal.Split('-')[0];
            SrcLangBox.SelectedValue = baseCode;
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Swap source → target (find exact or first prefix match: EN → EN-US, ZH → ZH-HANS)
        if (srcVal != null && srcVal != "auto")
        {
            var targetCode = LanguageData.TargetLanguages
                .FirstOrDefault(l => l.Code.Equals(srcVal, StringComparison.OrdinalIgnoreCase))?.Code
                ?? LanguageData.TargetLanguages
                    .FirstOrDefault(l => l.Code.StartsWith(srcVal + "-", StringComparison.OrdinalIgnoreCase))?.Code;
            TgtLangBox.SelectedValue = targetCode;
        }
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;
    }

    private async void RetranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = SettingsService.Instance.Current.ApiKey;
        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("缺少 API Key — 請先在設定中輸入", isError: true);
            return;
        }

        var srcLang = SrcLangBox.SelectedValue as string ?? "auto";
        var tgtLang = TgtLangBox.SelectedValue as string ?? "ZH-HANT";
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
        var lang = SrcLangBox.SelectedValue as string ?? "auto";
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus($"朗讀失敗：{ex.Message}", isError: true); }
    }

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslatedTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var lang = TgtLangBox.SelectedValue as string ?? "ZH-HANT";
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus($"朗讀失敗：{ex.Message}", isError: true); }
    }

    public void SetContent(string sourceText, string translatedText, string srcLang, string tgtLang)
    {
        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;
        StatusText.Text        = "";

        SrcLangBox.SelectedValue = srcLang;
        TgtLangBox.SelectedValue = tgtLang;
        if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = LanguageData.TargetLanguages.Count - 1;
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
}
