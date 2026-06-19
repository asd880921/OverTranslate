using System.Windows;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate;

public partial class TranslationWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly TranslationService _translationService = new();
    private readonly TtsService _tts = new();

    // Auto-translate: typing/edits restart this timer; it fires one translation once the user pauses.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private readonly DispatcherTimer _debounce;

    // True while we set the text/selectors programmatically, so those changes don't auto-translate.
    private bool _suppressAuto;
    // Monotonic id so a slow in-flight translation can't overwrite the result of a newer one.
    private int _seq;
    // Last input that translated successfully — lets us skip redundant identical re-translations.
    private (string Text, string Src, string Tgt, TranslationProvider Provider)? _lastDone;

    // The TTS button currently driving playback (so a second click stops instead of replaying).
    private System.Windows.Controls.Button? _ttsActiveBtn;

    public TranslationWindow(string sourceText, string translatedText, string sourceLang, string targetLang)
    {
        InitializeComponent();
        Icon = AppIconService.CreateWindowIcon();

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateNowAsync(); };

        _suppressAuto = true;
        InitializeSelectors(sourceLang, targetLang);
        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;
        _suppressAuto = false;

        _tts.StateChanged += OnTtsStateChanged;

        // Attach after initial values are set so initialization doesn't save or auto-translate
        SourceTextBox.TextChanged    += SourceTextBox_TextChanged;
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
    }

    private void SourceTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RequestTranslate();

    private void SrcLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
        RequestTranslate();
    }

    private void TgtLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
        RequestTranslate();
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        SaveProviderSelection(provider);
        RequestTranslate();
    }

    /// <summary>
    /// Schedules a debounced auto-translation. Programmatic edits are ignored (so opening from a
    /// screenshot doesn't re-translate), and an empty source instantly clears the output.
    /// </summary>
    private void RequestTranslate()
    {
        if (_suppressAuto) return;

        _debounce.Stop();
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _seq++;               // cancel any in-flight result
            TranslatedTextBox.Text = "";
            _lastDone = null;
            SetTranslating(false);
            SetStatus("", isError: false);
            ShowRetry(false);
            return;
        }
        _debounce.Start();
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowOrActivate(this);

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var srcVal = SrcLangBox.SelectedValue as string;
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Swap texts programmatically (suppressed); the language changes below trigger one re-translate
        _suppressAuto = true;
        (SourceTextBox.Text, TranslatedTextBox.Text) = (TranslatedTextBox.Text, SourceTextBox.Text);
        _suppressAuto = false;

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

        RequestTranslate(); // ensure the swapped direction is translated even if a language was unchanged
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e)
    {
        _lastDone = null;            // force a real re-translation even if input is unchanged
        _debounce.Stop();
        _ = TranslateNowAsync();
    }

    /// <summary>
    /// Translates the current source text with the chosen engine only (no hedge/fallback, per the
    /// manual window's design): a timeout/failure shows an error + retry rather than switching engines.
    /// Guarded by a sequence id so a stale result never overwrites a newer one.
    /// </summary>
    private async Task TranslateNowAsync()
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var apiKey = SettingsService.Instance.Current.ApiKey;
        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("缺少 API Key — 請先在設定中輸入", isError: true);
            ShowRetry(false);
            return;
        }

        var srcLang  = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        var tgtLang  = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        var provider = SettingsService.Instance.Current.Provider;

        var key = (text, srcLang, tgtLang, provider);
        if (_lastDone == key) return;   // identical to the last successful translation — skip

        var seq = ++_seq;
        ShowRetry(false);
        SetTranslating(true);

        try
        {
            var block = new OcrTextBlock(text, new System.Windows.Rect());
            var (results, _) = await _translationService.TranslateAsync([block], srcLang, tgtLang, apiKey, resilient: false);
            if (seq != _seq) return;    // a newer request superseded this one — let it own the UI

            TranslatedTextBox.Text = results.FirstOrDefault()?.TranslatedText ?? "";
            _lastDone = key;
            SetTranslating(false);
            SetStatus("", isError: false);
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;
            SetTranslating(false);
            SetStatus($"翻譯失敗：{ex.Message}", isError: true);
            ShowRetry(true);
        }
    }

    // Toggles the in-flight indicator: an indeterminate bar over the output plus an accent status line,
    // so "translating" is obvious where the user is looking (the 譯文 panel), not just a grey footer note.
    private void SetTranslating(bool on)
    {
        TranslatingBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            StatusText.Text       = "翻譯中…";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppAccent");
        }
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(SrcTtsBtn, SourceTextBox.Text,
                                LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string));

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(TgtTtsBtn, TranslatedTextBox.Text,
                                LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string));

    // Click the same button while it's speaking → stop. Click the other → switch playback to it.
    private async Task ToggleTtsAsync(System.Windows.Controls.Button btn, string text, string lang)
    {
        if (_tts.IsActive && _ttsActiveBtn == btn) { _tts.Stop(); return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        _ttsActiveBtn = btn;
        UpdateTtsIcons();           // show ⏹ immediately on click (don't wait for the fetch)
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus($"朗讀失敗：{ex.Message}", isError: true); }
    }

    // StateChanged only needs to handle "stopped/ended/failed" — start is reflected on click.
    private void OnTtsStateChanged(object? sender, EventArgs e)
    {
        if (!_tts.IsActive) { _ttsActiveBtn = null; UpdateTtsIcons(); }
    }

    private void UpdateTtsIcons()
    {
        SrcTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, SrcTtsBtn) ? "⏹" : "🔊";
        TgtTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, TgtTtsBtn) ? "⏹" : "🔊";
    }

    public void SetContent(string sourceText, string translatedText, string srcLang, string tgtLang)
    {
        // Content arrives already translated (from the screenshot flow) — show it as-is, don't re-call.
        _suppressAuto = true;
        _debounce.Stop();
        _seq++;

        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;
        SetTranslating(false);
        SetStatus("", isError: false);
        ShowRetry(false);

        SrcLangBox.SelectedValue = LanguageData.GetValidSourceCode(srcLang);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(tgtLang);

        // Treat the supplied translation as the current state so a later identical input won't re-call.
        _lastDone = (sourceText, LanguageData.GetValidSourceCode(srcLang),
                     LanguageData.GetValidTargetCode(tgtLang), SettingsService.Instance.Current.Provider);
        _suppressAuto = false;
    }

    private void ShowRetry(bool visible)
        => RetryBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
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
