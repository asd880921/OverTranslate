namespace OverTranslate.Models;

public record LangItem(string Code, string Display);
public record ProviderItem(TranslationProvider Provider, string Display, bool RequiresApiKey, string? Hint = null);

public static class LanguageData
{
    public const string DefaultSourceLanguage = "EN";
    public const string DefaultTargetLanguage = "ZH-HANT";

    public static readonly List<LangItem> SourceLanguages =
    [
        new("ZH-HANT", "繁體中文 Traditional Chinese"),
        new("ZH",      "簡體中文 Simplified Chinese"),
        new("EN",      "英語 English"),
        new("JA",      "日語 Japanese"),
        new("KO",      "韓語 Korean"),
        new("BG",      "保加利亞語 Bulgarian"),
        new("CS",      "捷克語 Czech"),
        new("DA",      "丹麥語 Danish"),
        new("DE",      "德語 German"),
        new("EL",      "希臘語 Greek"),
        new("ES",      "西班牙語 Spanish"),
        new("ET",      "愛沙尼亞語 Estonian"),
        new("FI",      "芬蘭語 Finnish"),
        new("FR",      "法語 French"),
        new("HU",      "匈牙利語 Hungarian"),
        new("ID",      "印尼語 Indonesian"),
        new("IT",      "義大利語 Italian"),
        new("LT",      "立陶宛語 Lithuanian"),
        new("LV",      "拉脫維亞語 Latvian"),
        new("NB",      "挪威語 Norwegian"),
        new("NL",      "荷蘭語 Dutch"),
        new("PL",      "波蘭語 Polish"),
        new("PT",      "葡萄牙語 Portuguese"),
        new("RO",      "羅馬尼亞語 Romanian"),
        new("RU",      "俄語 Russian"),
        new("SK",      "斯洛伐克語 Slovak"),
        new("SL",      "斯洛文尼亞語 Slovenian"),
        new("SV",      "瑞典語 Swedish"),
        new("TR",      "土耳其語 Turkish"),
        new("UK",      "烏克蘭語 Ukrainian"),
    ];

    public static readonly List<LangItem> OcrSourceLanguages =
    [
        new("ZH-HANT", "繁體中文 Traditional Chinese"),
        new("ZH",      "簡體中文 Simplified Chinese"),
        new("EN",      "英語 English"),
        new("JA",      "日語 Japanese"),
        new("KO",      "韓語 Korean"),
    ];

    public static readonly List<ProviderItem> Providers =
    [
        new(TranslationProvider.Google,  "Google 翻譯 (Web)", false, "傳統 API，整體響應速度較快，但較容易出現請求失敗或限制"),
        new(TranslationProvider.Google2, "Google 翻譯 (RPC)", false, "新版 RPC API，請求成功率較佳"),
        new(TranslationProvider.Bing,    "Bing 翻譯", false),
        new(TranslationProvider.Microsoft, "Microsoft 翻譯", false),
        new(TranslationProvider.Yandex,  "Yandex 翻譯", false, "Yandex 不支持繁體中文翻譯，使用繁體時會自動轉換為簡體中文"),
        new(TranslationProvider.DeepL,   "DeepL 翻譯", true, "需 API Key，可於 DeepL 官方申請（目前有提供免費方案，請至官網申請）"),
    ];

    /// <summary>
    /// Display name of a provider as shown in the selectors, for use in user-facing messages.
    /// Falls back to the enum name so a newly added provider never shows an empty label.
    /// </summary>
    public static string GetProviderDisplay(TranslationProvider provider) =>
        Providers.FirstOrDefault(p => p.Provider == provider)?.Display ?? provider.ToString();

    public static readonly List<LangItem> TargetLanguages =
    [
        new("ZH-HANT", "繁體中文 Traditional Chinese"),
        new("ZH-HANS", "簡體中文 Simplified Chinese"),
        new("EN-US",   "英語 English"),
        new("JA",      "日語 Japanese"),
        new("KO",      "韓語 Korean"),
        new("BG",      "保加利亞語 Bulgarian"),
        new("CS",      "捷克語 Czech"),
        new("DA",      "丹麥語 Danish"),
        new("DE",      "德語 German"),
        new("EL",      "希臘語 Greek"),
        new("ES",      "西班牙語 Spanish"),
        new("ET",      "愛沙尼亞語 Estonian"),
        new("FI",      "芬蘭語 Finnish"),
        new("FR",      "法語 French"),
        new("HU",      "匈牙利語 Hungarian"),
        new("ID",      "印尼語 Indonesian"),
        new("IT",      "義大利語 Italian"),
        new("LT",      "立陶宛語 Lithuanian"),
        new("LV",      "拉脫維亞語 Latvian"),
        new("NB",      "挪威語 Norwegian"),
        new("NL",      "荷蘭語 Dutch"),
        new("PL",      "波蘭語 Polish"),
        new("PT-BR",   "葡萄牙語 Portuguese"),
        new("RO",      "羅馬尼亞語 Romanian"),
        new("RU",      "俄語 Russian"),
        new("SK",      "斯洛伐克語 Slovak"),
        new("SL",      "斯洛文尼亞語 Slovenian"),
        new("SV",      "瑞典語 Swedish"),
        new("TR",      "土耳其語 Turkish"),
        new("UK",      "烏克蘭語 Ukrainian"),
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
            return DefaultSourceLanguage;

        var match = OcrSourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(sourceCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultSourceLanguage;
    }

    public static string GetValidTargetCode(string? targetCode)
    {
        if (string.IsNullOrWhiteSpace(targetCode))
            return DefaultTargetLanguage;

        var match = TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(targetCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultTargetLanguage;
    }
}
