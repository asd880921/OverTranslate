using System.IO;
using System.Windows.Media;
using GTranslate.Translators;
using NLog;

namespace OverTranslate.Services;

public class TtsService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly GoogleTranslator2    _google2   = new();
    private readonly GoogleTranslator     _google    = new();
    private readonly MicrosoftTranslator  _microsoft = new();
    private readonly BingTranslator       _bing      = new();
    private readonly YandexTranslator     _yandex    = new();
    private readonly MediaPlayer _player = new();
    private CancellationTokenSource? _cts;
    private bool _active;

    /// <summary>True while fetching audio or playing. Lets the UI toggle a play/stop button.</summary>
    public bool IsActive => _active;

    /// <summary>Raised (on the UI thread) whenever playback starts, ends, fails, or is stopped.</summary>
    public event EventHandler? StateChanged;

    public TtsService()
    {
        // Natural end / playback error must flip the button back to "play".
        _player.MediaEnded  += (_, _) => SetActive(false);
        _player.MediaFailed += (_, _) => SetActive(false);
    }

    private void SetActive(bool value)
    {
        if (_active == value) return;
        _active = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly string TempFile =
        Path.Combine(Path.GetTempPath(), "overtranslate_tts.mp3");

    /// <summary>Stops any in-flight fetch and playback.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        System.Windows.Application.Current.Dispatcher.Invoke(() => { _player.Stop(); _player.Close(); });
        SetActive(false);
    }

    public async Task SpeakAsync(string text, string langCode)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        System.Windows.Application.Current.Dispatcher.Invoke(() => { _player.Stop(); _player.Close(); });
        SetActive(true);

        var providers = BuildProviders(text, langCode);
        Exception? lastEx = null;

        foreach (var (name, speak) in providers)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                Log.Debug("TTS trying {Provider}, lang={Lang}", name, langCode);
                var stream = await speak();
                token.ThrowIfCancellationRequested();

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, token);
                await File.WriteAllBytesAsync(TempFile, ms.ToArray(), token);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _player.Open(new Uri(TempFile));
                    _player.Play();
                });

                Log.Debug("TTS success via {Provider}", name);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn(ex, "TTS provider {Provider} failed, trying next", name);
                lastEx = ex;
            }
        }

        // Every provider failed (and we weren't cancelled) — clear state so the button resets.
        if (!token.IsCancellationRequested) SetActive(false);
        if (lastEx != null) throw lastEx;
    }

    private List<(string name, Func<Task<Stream>> speak)> BuildProviders(string text, string langCode)
    {
        var gLang = MapGoogle(langCode);
        var bLang = MapBing(langCode);
        var yLang = MapYandex(langCode);

        var mLang = MapMicrosoft(langCode);

        return
        [
            ("Google2",    () => _google2.TextToSpeechAsync(text, gLang, false)),
            ("Google",     () => _google.TextToSpeechAsync(text, gLang)),
            ("Microsoft",  () => _microsoft.TextToSpeechAsync(text, mLang)),
            ("Bing",       () => _bing.TextToSpeechAsync(text, bLang)),
            ("Yandex",     () => _yandex.TextToSpeechAsync(text, yLang)),
        ];
    }

    private static string MapGoogle(string code) => code.ToUpperInvariant() switch
    {
        "ZH" or "ZH-HANS" or "AUTO" => "zh-CN",
        "ZH-HANT"                    => "zh-TW",
        "JA"                         => "ja",
        "KO"                         => "ko",
        "EN" or "EN-US" or "EN-GB"   => "en",
        "DE"                         => "de",
        "FR"                         => "fr",
        "ES"                         => "es",
        "IT"                         => "it",
        "PT" or "PT-BR"              => "pt",
        "RU"                         => "ru",
        "UK"                         => "uk",
        "PL"                         => "pl",
        "NL"                         => "nl",
        "TR"                         => "tr",
        _                            => "en",
    };

    private static string MapBing(string code) => code.ToUpperInvariant() switch
    {
        "ZH" or "ZH-HANS" or "AUTO" => "zh-Hans",
        "ZH-HANT"                    => "zh-Hant",
        "JA"                         => "ja",
        "KO"                         => "ko",
        "EN" or "EN-US" or "EN-GB"   => "en",
        "DE"                         => "de",
        "FR"                         => "fr",
        "ES"                         => "es",
        "IT"                         => "it",
        "PT" or "PT-BR"              => "pt",
        "RU"                         => "ru",
        "UK"                         => "uk",
        "PL"                         => "pl",
        "NL"                         => "nl",
        "TR"                         => "tr",
        _                            => "en",
    };

    private static string MapMicrosoft(string code) => MapBing(code);

    private static string MapYandex(string code) => code.ToUpperInvariant() switch
    {
        "ZH" or "ZH-HANS" or "ZH-HANT" or "AUTO" => "zh",
        "JA"                                       => "ja",
        "KO"                                       => "ko",
        "EN" or "EN-US" or "EN-GB"                 => "en",
        "DE"                                       => "de",
        "FR"                                       => "fr",
        "ES"                                       => "es",
        "IT"                                       => "it",
        "PT" or "PT-BR"                            => "pt",
        "RU"                                       => "ru",
        "UK"                                       => "uk",
        "PL"                                       => "pl",
        "NL"                                       => "nl",
        "TR"                                       => "tr",
        _                                          => "en",
    };

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _google2.Dispose();
        _google.Dispose();
        _microsoft.Dispose();
        _bing.Dispose();
        _yandex.Dispose();
        System.Windows.Application.Current.Dispatcher.Invoke(() => { _player.Stop(); _player.Close(); });
    }
}
