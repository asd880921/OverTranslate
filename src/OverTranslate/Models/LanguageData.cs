namespace OverTranslate.Models;

/// <param name="Name">The language's name in the interface's own language, which is what a
/// message says it back with.</param>
/// <param name="English">
/// Its English name, carried separately rather than baked into one label: the pickers show both so
/// a user scanning a long list can find theirs either way, while somewhere with one line to spare
/// wants only the first. Composing here means neither has to take a string apart to get at one.
/// </param>
public record LangItem(string Code, string Name, string English)
{
    public string Display => string.IsNullOrEmpty(English) ? Name : $"{Name} {English}";
}
public record ProviderItem(TranslationProvider Provider, string Display, bool RequiresApiKey, string? Hint = null);

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
        new(AutomaticSourceLanguage, "自動（中英日）", ""),
        new("EN",      "英語", "English"),
        new("JA",      "日語", "Japanese"),
        new("KO",      "韓語", "Korean"),
        new("ZH",      "簡體中文", "Simplified Chinese"),
        new("ZH-HANT", "繁體中文", "Traditional Chinese"),
    ];

    public static readonly List<ProviderItem> Providers = CreateProviders();

    private static List<ProviderItem> CreateProviders()
    {
        List<ProviderItem> providers =
        [
        new(TranslationProvider.Google,  "Google 翻譯 (Web)", false, "傳統 API，整體響應速度較快，但較容易出現請求失敗或限制"),
        new(TranslationProvider.Google2, "Google 翻譯 (RPC)", false, "新版 RPC API，請求成功率較佳"),
        new(TranslationProvider.Bing,    "Bing 翻譯", false),
        new(TranslationProvider.Microsoft, "Microsoft 翻譯", false),
        new(TranslationProvider.Yandex,  "Yandex 翻譯", false, "Yandex 不支持繁體中文翻譯，使用繁體時會自動轉換為簡體中文"),
        new(TranslationProvider.DeepL,   "DeepL 翻譯", true, "需 API Key，可於 DeepL 官方申請（目前有提供免費方案，請至官網申請）"),
        ];
        if (Services.LocalNmt.LocalNmtBootstrap.IsConfigured)
            providers.Add(new(TranslationProvider.LocalNmt, "本機翻譯 (Bergamot)", false,
                "實驗性本機模型；翻譯內容不會傳送到雲端"));
        return providers;
    }

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
