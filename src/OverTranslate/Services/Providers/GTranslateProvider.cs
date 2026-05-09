using GTranslate;
using GTranslate.Translators;

namespace OverTranslate.Services.Providers;

public class GTranslateProvider : ITranslationProvider
{
    private readonly ITranslator _translator;
    private readonly Dictionary<string, string> _targetOverrides;
    private readonly GoogleTranslator _googleTranslator;
    private readonly GoogleTranslator2 _googleTranslator2;

    public GTranslateProvider(ITranslator translator, Dictionary<string, string>? targetOverrides = null)
    {
        _translator     = translator;
        _targetOverrides = targetOverrides ?? [];
    }

    public bool RequiresApiKey => false;

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

    private static string MapDetectedToDeepL(string iso6391) => iso6391.ToUpperInvariant() switch
    {
        "NO" => "NB",
        _    => iso6391.ToUpperInvariant()
    };

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey)
    {
        if (blocks.Count == 0) return ([], "");

        if (_targetOverrides.TryGetValue(targetLang, out var overrideLang))
            targetLang = overrideLang;

        var toCode   = MapToGTranslate(targetLang);
        var fromCode = sourceLang == "auto" ? null : MapToGTranslate(sourceLang);

        var tasks   = blocks.Select(b => _translator.TranslateAsync(b.Text, toCode, fromCode));
        var results = await Task.WhenAll(tasks);

        var langVotes  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var translated = new List<TranslatedBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var r       = results[i];
            var detLang = r.SourceLanguage?.ISO6391 ?? "";
            if (!string.IsNullOrEmpty(detLang))
            {
                var mapped = MapDetectedToDeepL(detLang);
                langVotes[mapped] = langVotes.GetValueOrDefault(mapped) + 1;
            }
            translated.Add(new TranslatedBlock(blocks[i].Text, r.Translation, blocks[i].Bounds));
        }

        string detectedLang = langVotes.Count > 0 ? langVotes.MaxBy(kv => kv.Value).Key : "";
        return (translated, detectedLang);
    }
}
