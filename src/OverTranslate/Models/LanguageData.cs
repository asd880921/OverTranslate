using OverTranslate.Services;

namespace OverTranslate.Models;

/// <param name="Name">The language's name in the interface's own language, which is what a
/// message says it back with.</param>
/// <param name="English">
/// Its English name, carried separately rather than baked into one label: the pickers show both so
/// a user scanning a long list can find theirs either way, while somewhere with one line to spare
/// wants only the first. Composing here means neither has to take a string apart to get at one.
/// </param>
/// <param name="StandsAlone">
/// True when <paramref name="Name"/> and <paramref name="English"/> are each a complete label on
/// their own, so the Chinese interface shows the name alone instead of the usual pair. For the OCR
/// picker's automatic entry, whose name already carries its own parenthetical.
/// </param>
public record LangItem(string Code, string Name, string English, bool StandsAlone = false)
{
    /// <summary>
    /// The label for the pickers, in whichever language the interface is currently in.
    /// </summary>
    /// <remarks>
    /// The Chinese interface shows both names, which is what the pair was carried separately for.
    /// The English one shows only <see cref="English"/>: "英語 English" beside an English menu is
    /// noise to a reader who cannot read the first half, and the doubled-up label is what pushed
    /// these combo boxes wide in the first place.
    /// </remarks>
    public string Display
    {
        get
        {
            if (LocalizationService.Current == LocalizationService.English)
                return string.IsNullOrEmpty(English) ? Name : English;

            return StandsAlone || string.IsNullOrEmpty(English) ? Name : $"{Name} {English}";
        }
    }
}
/// <param name="DisplayKey">Resource key for the name shown in the pickers.</param>
/// <param name="HintKey">Resource key for the line under the picker, or null for no hint.</param>
/// <remarks>
/// Keys rather than text: the list is static, the interface language is not, and a record built
/// once at type-initialisation would otherwise keep whichever language the app started in.
/// Resolving in the properties means callers still just read <see cref="Display"/>.
/// </remarks>
public record ProviderItem(
    TranslationProvider Provider, string DisplayKey, bool RequiresApiKey, string? HintKey = null)
{
    public string Display => LocalizationService.Get(DisplayKey);
    public string? Hint => HintKey is null ? null : LocalizationService.Get(HintKey);
}

public static class LanguageData
{
    public const string AutomaticSourceLanguage = "AUTO";
    public const string DefaultSourceLanguage = AutomaticSourceLanguage;
    public const string DefaultOcrSourceLanguage = AutomaticSourceLanguage;
    public const string DefaultTargetLanguage = "ZH-HANT";

    public static readonly List<LangItem> SourceLanguages =
    [
        new(AutomaticSourceLanguage, "自動", "Automatic"),
        new("EN",      "英語", "English"),
        new("JA",      "日語", "Japanese"),
        new("KO",      "韓語", "Korean"),
        new("ZH",      "簡體中文", "Simplified Chinese"),
        new("ZH-HANT", "繁體中文", "Traditional Chinese"),
        new("BG",      "保加利亞語", "Bulgarian"),
        new("CS",      "捷克語", "Czech"),
        new("DA",      "丹麥語", "Danish"),
        new("DE",      "德語", "German"),
        new("EL",      "希臘語", "Greek"),
        new("ES",      "西班牙語", "Spanish"),
        new("ET",      "愛沙尼亞語", "Estonian"),
        new("FI",      "芬蘭語", "Finnish"),
        new("FR",      "法語", "French"),
        new("HU",      "匈牙利語", "Hungarian"),
        new("ID",      "印尼語", "Indonesian"),
        new("IT",      "義大利語", "Italian"),
        new("LT",      "立陶宛語", "Lithuanian"),
        new("LV",      "拉脫維亞語", "Latvian"),
        new("NB",      "挪威語", "Norwegian"),
        new("NL",      "荷蘭語", "Dutch"),
        new("PL",      "波蘭語", "Polish"),
        new("PT",      "葡萄牙語", "Portuguese"),
        new("RO",      "羅馬尼亞語", "Romanian"),
        new("RU",      "俄語", "Russian"),
        new("SK",      "斯洛伐克語", "Slovak"),
        new("SL",      "斯洛文尼亞語", "Slovenian"),
        new("SV",      "瑞典語", "Swedish"),
        new("TR",      "土耳其語", "Turkish"),
        new("UK",      "烏克蘭語", "Ukrainian"),
    ];

    public static readonly List<LangItem> OcrSourceLanguages =
    [
        new(AutomaticSourceLanguage, "自動（中英日）", "Automatic (ZH/EN/JA)", StandsAlone: true),
        new("EN",      "英語", "English"),
        new("JA",      "日語", "Japanese"),
        new("KO",      "韓語", "Korean"),
        new("ZH",      "簡體中文", "Simplified Chinese"),
        new("ZH-HANT", "繁體中文", "Traditional Chinese"),
    ];

    public static readonly List<ProviderItem> Providers =
    [
        new(TranslationProvider.Google,    "S.Provider.Google",    false, "S.Provider.GoogleHint"),
        new(TranslationProvider.Google2,   "S.Provider.Google2",   false, "S.Provider.Google2Hint"),
        new(TranslationProvider.Bing,      "S.Provider.Bing",      false),
        new(TranslationProvider.Microsoft, "S.Provider.Microsoft", false),
        new(TranslationProvider.DeepL,     "S.Provider.DeepL",     true,  "S.Provider.DeepLHint"),
        new(TranslationProvider.OpenAI,    "S.Provider.OpenAI",    false, "S.Provider.OpenAIHint"),
    ];

    /// <summary>
    /// Display name of a provider as shown in the selectors, for use in user-facing messages.
    /// Falls back to the enum name so a newly added provider never shows an empty label.
    /// </summary>
    public static string GetProviderDisplay(TranslationProvider provider) =>
        Providers.FirstOrDefault(p => p.Provider == provider)?.Display ?? provider.ToString();

    public static readonly List<LangItem> TargetLanguages =
    [
        new("EN-US",   "英語", "English"),
        new("JA",      "日語", "Japanese"),
        new("KO",      "韓語", "Korean"),
        new("ZH-HANS", "簡體中文", "Simplified Chinese"),
        new("ZH-HANT", "繁體中文", "Traditional Chinese"),
        new("BG",      "保加利亞語", "Bulgarian"),
        new("CS",      "捷克語", "Czech"),
        new("DA",      "丹麥語", "Danish"),
        new("DE",      "德語", "German"),
        new("EL",      "希臘語", "Greek"),
        new("ES",      "西班牙語", "Spanish"),
        new("ET",      "愛沙尼亞語", "Estonian"),
        new("FI",      "芬蘭語", "Finnish"),
        new("FR",      "法語", "French"),
        new("HU",      "匈牙利語", "Hungarian"),
        new("ID",      "印尼語", "Indonesian"),
        new("IT",      "義大利語", "Italian"),
        new("LT",      "立陶宛語", "Lithuanian"),
        new("LV",      "拉脫維亞語", "Latvian"),
        new("NB",      "挪威語", "Norwegian"),
        new("NL",      "荷蘭語", "Dutch"),
        new("PL",      "波蘭語", "Polish"),
        new("PT-BR",   "葡萄牙語", "Portuguese"),
        new("RO",      "羅馬尼亞語", "Romanian"),
        new("RU",      "俄語", "Russian"),
        new("SK",      "斯洛伐克語", "Slovak"),
        new("SL",      "斯洛文尼亞語", "Slovenian"),
        new("SV",      "瑞典語", "Swedish"),
        new("TR",      "土耳其語", "Turkish"),
        new("UK",      "烏克蘭語", "Ukrainian"),
    ];

    public static string? MapTargetToSourceCode(string targetCode)
    {
        if (string.IsNullOrWhiteSpace(targetCode)) return null;

        var mapped = targetCode.ToUpperInvariant() switch
        {
            "ZH-HANT" => "ZH-HANT",
            "ZH-HANS" => "ZH",
            _ => targetCode.Split('-')[0]
        };

        return SourceLanguages.Any(l => l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase))
            ? SourceLanguages.First(l => l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase)).Code
            : null;
    }

    public static string? MapSourceToTargetCode(string sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode)) return null;

        var mapped = sourceCode.ToUpperInvariant() switch
        {
            "ZH-HANT" => "ZH-HANT",
            "ZH" => "ZH-HANS",
            "EN" => "EN-US",
            _ => sourceCode
        };

        var exact = TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Code;

        var prefix = TargetLanguages.FirstOrDefault(l =>
            l.Code.StartsWith(mapped + "-", StringComparison.OrdinalIgnoreCase));
        return prefix?.Code;
    }

    public static bool IsAutomaticSource(string? sourceCode) =>
        AutomaticSourceLanguage.Equals(sourceCode, StringComparison.OrdinalIgnoreCase);

    public static string GetValidSourceCode(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return DefaultSourceLanguage;

        var match = SourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(sourceCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultSourceLanguage;
    }

    public static string GetValidOcrSourceCode(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return DefaultOcrSourceLanguage;

        var match = OcrSourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(sourceCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultOcrSourceLanguage;
    }

    public static string GetValidTargetCode(string? targetCode)
    {
        if (string.IsNullOrWhiteSpace(targetCode))
            return DefaultTargetLanguage;

        var match = TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(targetCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultTargetLanguage;
    }

    /// <summary>
    /// A language's name for saying back to the user what is in effect — the interface's own name
    /// for it, without the English one the pickers also carry, because a message has one line and
    /// the reader already knows which language they are reading in. Falls back to the code, which
    /// is still readable, rather than to an empty string.
    /// </summary>
    public static string GetSourceName(string? code) =>
        OcrSourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? code ?? "";

    /// <inheritdoc cref="GetSourceName"/>
    public static string GetTargetName(string? code) =>
        TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? code ?? "";
}
