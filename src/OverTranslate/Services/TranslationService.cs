using GTranslate.Translators;
using OverTranslate.Models;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services;

public record TranslatedBlock(
    string OriginalText,
    string TranslatedText,
    System.Windows.Rect Bounds,
    System.Windows.Media.Color BackgroundColor = default,
    System.Windows.Media.Color TextColor = default);

public class TranslationService
{
    private readonly GTranslateProvider _google  = new(new GoogleTranslator());
    private readonly GTranslateProvider _google2 = new(new GoogleTranslator2());
    private readonly GTranslateProvider _bing    = new(new BingTranslator());
    private readonly GTranslateProvider _yandex  = new(new YandexTranslator(), new() { { "ZH-HANT", "ZH-HANS" } });
    private readonly DeepLProvider      _deepL   = new();

    private ITranslationProvider Current => SettingsService.Instance.Current.Provider switch
    {
        TranslationProvider.Google => _google,
        TranslationProvider.Bing    => _bing,
        TranslationProvider.Yandex  => _yandex,
        TranslationProvider.DeepL   => _deepL,
        _                           => _google2,
    };

    public bool RequiresApiKey => Current.RequiresApiKey;

    public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey) =>
        Current.TranslateAsync(blocks, sourceLang, targetLang, apiKey);
}
