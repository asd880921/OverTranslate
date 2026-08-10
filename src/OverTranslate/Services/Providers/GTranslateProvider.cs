using GTranslate;
using GTranslate.Translators;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

public class GTranslateProvider : ITranslationProvider
{
    private readonly ITranslator _translator;
    private readonly Dictionary<string, string> _targetOverrides;

    public GTranslateProvider(ITranslator translator, Dictionary<string, string>? targetOverrides = null)
    {
        _translator     = translator;
        _targetOverrides = targetOverrides ?? [];
    }

    public bool RequiresApiKey => false;

    // Friendly engine name (e.g. "GoogleTranslator2", "BingTranslator") for diagnostics/logging.
    public string Name => _translator.Name;

    // Maps DeepL-style codes to BCP-47 codes used by GTranslate
    private static readonly Dictionary<string, string> ToGTranslate = new(StringComparer.OrdinalIgnoreCase)
    {
        { "EN",      "en"    }, { "EN-US",   "en"    }, { "EN-GB",   "en"    },
        { "ZH",      "zh-CN" }, { "ZH-HANS", "zh-CN" }, { "ZH-HANT", "zh-TW" },
        { "PT",      "pt"    }, { "PT-BR",   "pt"    }, { "PT-PT",   "pt"    },
        { "DE",      "de"    }, { "FR",      "fr"    }, { "ES",      "es"    },
        { "IT",      "it"    }, { "NL",      "nl"    }, { "PL",      "pl"    },
        { "RU",      "ru"    }, { "JA",      "ja"    }, { "KO",      "ko"    },
        { "CS",      "cs"    }, { "DA",      "da"    }, { "EL",      "el"    },
        { "ET",      "et"    }, { "FI",      "fi"    }, { "HU",      "hu"    },
        { "ID",      "id"    }, { "LT",      "lt"    }, { "LV",      "lv"    },
        { "NB",      "no"    }, { "RO",      "ro"    }, { "SK",      "sk"    },
        { "SL",      "sl"    }, { "SV",      "sv"    }, { "TR",      "tr"    },
        { "UK",      "uk"    }, { "BG",      "bg"    },
    };

    private static string MapToGTranslate(string deepLCode) =>
        ToGTranslate.TryGetValue(deepLCode, out var code) ? code : deepLCode.ToLowerInvariant().Split('-')[0];

    internal static string? MapSourceToGTranslate(string sourceLang) =>
        LanguageData.IsAutomaticSource(sourceLang) ? null : MapToGTranslate(sourceLang);

    private static string MapDetectedToDeepL(string iso6391) => iso6391.ToUpperInvariant() switch
    {
        "NO" => "NB",
        _    => iso6391.ToUpperInvariant()
    };

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var tasks   = blocks.Select(b => TranslateOneAsync(b.Text, sourceLang, targetLang, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var langVotes  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var translated = new List<TranslatedBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var (translation, detLang) = results[i];
            if (!string.IsNullOrEmpty(detLang))
                langVotes[detLang] = langVotes.GetValueOrDefault(detLang) + 1;
            translated.Add(new TranslatedBlock(blocks[i].Text, translation, blocks[i].Bounds, blocks[i].Lines, blocks[i].SourceGlyphHeight));
        }

        string detectedLang = langVotes.Count > 0 ? langVotes.MaxBy(kv => kv.Value).Key : "";
        return (translated, detectedLang);
    }

    // Translates a single text fragment. Detected language is returned as a DeepL-style code.
    // Throws if the underlying free endpoint fails — the caller (ResilientProvider) decides on fallback.
    //
    // GTranslate's ITranslator.TranslateAsync takes no CancellationToken, so a request already in
    // flight cannot be aborted. The token is honoured at the only point where it still helps: before
    // the call is made. That is what keeps an abandoned batch from issuing the requests it has not
    // started yet, including every hedged backup ResilientProvider would have launched.
    public async Task<(string Translation, string DetectedLang)> TranslateOneAsync(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_targetOverrides.TryGetValue(targetLang, out var overrideLang))
            targetLang = overrideLang;

        var toCode   = MapToGTranslate(targetLang);
        var fromCode = MapSourceToGTranslate(sourceLang);

        var r       = await _translator.TranslateAsync(text, toCode, fromCode);
        var detLang = r.SourceLanguage?.ISO6391 ?? "";
        var mapped  = string.IsNullOrEmpty(detLang) ? "" : MapDetectedToDeepL(detLang);
        return (r.Translation, mapped);
    }
}
