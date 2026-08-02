using System.Net.Http;
using GTranslate.Translators;
using OverTranslate.Models;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services;

public record TranslatedBlock(
    string OriginalText,
    string TranslatedText,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    double? SourceGlyphHeight = null,
    System.Windows.Media.Color BackgroundColor = default,
    System.Windows.Media.Color TextColor = default);

public class TranslationService
{
    // Shared HttpClient so a hung free endpoint fails fast instead of stalling the whole batch.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly GTranslateProvider _google    = new(new GoogleTranslator(Http));
    private readonly GTranslateProvider _google2   = new(new GoogleTranslator2(Http));
    private readonly GTranslateProvider _bing      = new(new BingTranslator(Http));
    private readonly GTranslateProvider _microsoft = new(new MicrosoftTranslator(Http));
    private readonly GTranslateProvider _yandex    = new(new YandexTranslator(Http), new() { { "ZH-HANT", "ZH-HANS" } });
    private readonly DeepLProvider      _deepL     = new();

    // Per-engine resilient wrappers: the user's choice is the primary, the other reliable
    // keyless engines act as hedged backups so one slow/throttled endpoint can't stall the batch.
    private readonly ResilientProvider _googleR;
    private readonly ResilientProvider _google2R;
    private readonly ResilientProvider _bingR;
    private readonly ResilientProvider _microsoftR;
    private readonly ResilientProvider _yandexR;

    public TranslationService()
    {
        // Google2/Bing/Microsoft are the most reliable free endpoints — use them as the backup pool.
        _google2R   = new ResilientProvider([_google2, _bing, _microsoft]);
        _bingR      = new ResilientProvider([_bing, _google2, _microsoft]);
        _microsoftR = new ResilientProvider([_microsoft, _google2, _bing]);
        _googleR    = new ResilientProvider([_google, _google2, _bing]);
        _yandexR    = new ResilientProvider([_yandex, _google2, _bing]);
    }

    // Resilient (hedged + fallback) provider for the user's current choice.
    private ITranslationProvider Current => SettingsService.Instance.Current.Provider switch
    {
        TranslationProvider.Google    => _googleR,
        TranslationProvider.Bing      => _bingR,
        TranslationProvider.Microsoft => _microsoftR,
        TranslationProvider.Yandex    => _yandexR,
        TranslationProvider.DeepL     => _deepL,
        _                             => _google2R,
    };

    // Single chosen engine, no hedging/fallback — a timeout/failure surfaces directly to the caller.
    private ITranslationProvider Direct => SettingsService.Instance.Current.Provider switch
    {
        TranslationProvider.Google    => _google,
        TranslationProvider.Bing      => _bing,
        TranslationProvider.Microsoft => _microsoft,
        TranslationProvider.Yandex    => _yandex,
        TranslationProvider.DeepL     => _deepL,
        _                             => _google2,
    };

    public bool RequiresApiKey => Current.RequiresApiKey;

    /// <summary>
    /// Which engine(s) actually served the most recent translation. Null for providers that have
    /// no fallback concept (e.g. DeepL), so the UI can keep the engine badge hidden.
    /// </summary>
    public EngineUsage? LastEngineUsage { get; private set; }

    /// <param name="resilient">
    /// true (default) uses the hedged/fallback provider; false sends to the single chosen engine only,
    /// so a timeout/failure throws straight to the caller (used by the manual translation window).
    /// </param>
    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey, bool resilient = true,
        CancellationToken cancellationToken = default)
    {
        var provider = resilient ? Current : Direct;
        var result   = await provider.TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);
        LastEngineUsage = (provider as ResilientProvider)?.LastUsage;
        return result;
    }
}
