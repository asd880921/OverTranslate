using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Translation;

public partial class TranslationPage : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly TranslationService _translationService = new();
    private readonly TtsService _tts = new();

    // Auto-translate: typing/edits restart this timer; it fires one translation once the user pauses.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private readonly DispatcherTimer _debounce;

    // True while we set the text/selectors programmatically, so those changes don't auto-translate.
    private bool _suppressAuto;
    // True while shared settings are being reflected into the selectors, so their change events do
    // not write the same values back to disk one field at a time.
    private bool _reloadingPreferences;
    // Monotonic id so a slow in-flight translation can't overwrite the result of a newer one.
    private int _seq;
    // Last input that translated successfully — lets us skip redundant identical re-translations.
    private (string Text, string Src, string Tgt, TranslationProvider Provider)? _lastDone;

    // The TTS button currently driving playback (so a second click stops instead of replaying).
    private Button? _ttsActiveBtn;

    public TranslationPage()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateNowAsync(); };

        var settings = SettingsService.Instance.Current;

        _suppressAuto = true;
        InitializeSelectors(settings.SourceLanguage, settings.TargetLanguage);
        _suppressAuto = false;

        _tts.StateChanged += OnTtsStateChanged;

        // Attach after initial values are set so initialization doesn't save or auto-translate
        SourceTextBox.TextChanged    += SourceTextBox_TextChanged;
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
    }

    /// <summary>
    /// Re-reads the shared translation preferences after another page changes them.
    /// </summary>
    public void Reload()
    {
        var settings = SettingsService.Instance.Current;
        var sourceLanguage = LanguageData.GetValidSourceCode(settings.SourceLanguage);
        var targetLanguage = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        var provider = settings.Provider;
        var selectionChanged =
            !Equals(SrcLangBox.SelectedValue, sourceLanguage) ||
            !Equals(TgtLangBox.SelectedValue, targetLanguage) ||
            !Equals(ProviderBox.SelectedValue, provider);

        _suppressAuto = true;
        _reloadingPreferences = true;
        try
        {
            SrcLangBox.SelectedValue = sourceLanguage;
            TgtLangBox.SelectedValue = targetLanguage;
            ProviderBox.SelectedValue = provider;
            if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        }
        finally
        {
            _reloadingPreferences = false;
            _suppressAuto = false;
        }

        if (selectionChanged)
            RequestTranslate();
    }

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        => RequestTranslate();

    private void SrcLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingPreferences) SaveCurrentLanguageSelection();
        RequestTranslate();
    }

    private void TgtLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingPreferences) SaveCurrentLanguageSelection();
        RequestTranslate();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        if (!_reloadingPreferences) SaveProviderSelection(provider);
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
    /// manual page's design): a timeout/failure shows an error + retry rather than switching engines.
    /// Guarded by a sequence id so a stale result never overwrites a newer one.
    /// </summary>
    private async Task TranslateNowAsync()
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var apiKey = SettingsService.Instance.Current.ApiKey;
        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus(LocalizationService.Get("S.Translation.MissingApiKey"), isError: true);
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
            var block = new OcrTextBlock(text, new Rect());
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

            // This page sends to the chosen engine only (resilient: false), so any failure lands
            // here verbatim — and the free endpoints throw whatever their internals produce (e.g.
            // GTranslate surfacing a raw System.Text.Json parse error when Google's undocumented
            // RPC endpoint answers with something that isn't JSON). Catch everything and lead with
            // a line that says what the user can actually do; keep the original text underneath
            // so the cause is still reportable.
            SetStatus(
                LocalizationService.Format(
                    "S.Translation.ProviderUnavailable",
                    LanguageData.GetProviderDisplay(provider), ex.Message),
                isError: true);
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
            StatusText.Text       = LocalizationService.Get("S.Translation.Translating");
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
    private async Task ToggleTtsAsync(Button btn, string text, string lang)
    {
        if (_tts.IsActive && _ttsActiveBtn == btn) { _tts.Stop(); return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        _ttsActiveBtn = btn;
        UpdateTtsIcons();           // show ⏹ immediately on click (don't wait for the fetch)
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus(LocalizationService.Format("S.Translation.SpeakFailed", ex.Message), isError: true); }
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

    /// <summary>Releases the debounce timer and TTS playback when the shell window is destroyed.</summary>
    public void Teardown()
    {
        _debounce.Stop();
        _tts.Dispose();
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
        LocalizationService.BindLocalizedItems(SrcLangBox,  LanguageData.SourceLanguages);
        LocalizationService.BindLocalizedItems(TgtLangBox,  LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

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
