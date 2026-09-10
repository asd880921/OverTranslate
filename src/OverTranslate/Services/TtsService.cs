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
    private MediaPlayer? _player;
    private string? _currentFile;
    private CancellationTokenSource? _cts;
    private bool _active;

    /// <summary>True while fetching audio or playing. Lets the UI toggle a play/stop button.</summary>
    public bool IsActive => _active;

    /// <summary>Raised (on the UI thread) whenever playback starts, ends, fails, or is stopped.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Creates the player on demand. A MediaPlayer that has raised MediaFailed cannot be trusted to
    /// play again — depending on the error it can go silently dead, and then every later Open/Play on
    /// that instance does nothing at all, which is exactly the "no sound until I reopen 取詞翻譯" the
    /// user is left with. So a failed player is thrown away and the next request gets a fresh one.
    /// UI thread only.
    /// </summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player != null) return _player;

        var player = new MediaPlayer();
        // Natural end / playback error must flip the button back to "play".
        player.MediaEnded += (_, _) =>
        {
            if (!ReferenceEquals(_player, player)) return;
            // Close() releases the temp file, which the player holds open until its next Open().
            player.Close();
            DeleteCurrentFile();
            SetActive(false);
        };
        player.MediaFailed += (_, e) =>
        {
            Log.Warn(e.ErrorException, "TTS playback failed, discarding player");
            player.Close();
            if (ReferenceEquals(_player, player))
            {
                _player = null;
                DeleteCurrentFile();
            }
            SetActive(false);
        };

        _player = player;
        return player;
    }

    /// <summary>Stops playback and releases the file the player was holding. UI thread only.</summary>
    private void ClosePlayer()
    {
        if (_player != null)
        {
            _player.Stop();
            _player.Close();
        }
        DeleteCurrentFile();
    }

    private void SetActive(bool value)
    {
        if (_active == value) return;
        _active = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private const string TempPrefix = "overtranslate_tts_";
    private static int _staleFilesSwept;

    /// <summary>
    /// Every playback gets its own file: the player keeps the previous one open, and re-opening the
    /// same path also runs into the media stack caching that URI.
    /// </summary>
    private static string NewTempFile() =>
        Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.mp3");

    private void DeleteCurrentFile()
    {
        var file = Interlocked.Exchange(ref _currentFile, null);
        if (file == null) return;
        try { File.Delete(file); }
        catch (Exception ex) { Log.Debug(ex, "Could not delete TTS temp file {File}", file); }
    }

    /// <summary>Removes files an earlier crash left behind. Old enough that no live player holds them.</summary>
    private static void SweepStaleFilesOnce()
    {
        if (Interlocked.Exchange(ref _staleFilesSwept, 1) != 0) return;
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), TempPrefix + "*.mp3"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* still in use by another process, or already gone */ }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "TTS temp sweep failed"); }
    }

    /// <summary>Stops any in-flight fetch and playback.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(false);
    }

    public async Task SpeakAsync(string text, string langCode)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(true);

        SweepStaleFilesOnce();

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
                var file = NewTempFile();
                await File.WriteAllBytesAsync(file, ms.ToArray(), token);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    DeleteCurrentFile();
                    _currentFile = file;
                    var player = EnsurePlayer();
                    player.Open(new Uri(file));
                    player.Play();
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
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
    }
}
