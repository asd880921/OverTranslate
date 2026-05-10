namespace OverTranslate.Models;

public record LangItem(string Code, string Display);
public record ProviderItem(TranslationProvider Provider, string Display, bool RequiresApiKey, string? Hint = null);
public record OcrEngineItem(OcrEngineType Engine, string Display, string? Hint = null);

public static class LanguageData
{
    public static readonly List<LangItem> SourceLanguages =
    [
        new("ZH-HANT", "Chinese Traditional 繁體中文"),
        new("ZH",      "Chinese Simplified 簡體中文"),
        new("EN",      "English 英語"),
        new("JA",      "Japanese 日語"),
        new("KO",      "Korean 韓語"),
        new("BG",      "Bulgarian 保加利亞語"),
        new("CS",      "Czech 捷克語"),
        new("DA",      "Danish 丹麥語"),
        new("DE",      "German 德語"),
        new("EL",      "Greek 希臘語"),
        new("ES",      "Spanish 西班牙語"),
        new("ET",      "Estonian 愛沙尼亞語"),
        new("FI",      "Finnish 芬蘭語"),
        new("FR",      "French 法語"),
        new("HU",      "Hungarian 匈牙利語"),
        new("ID",      "Indonesian 印尼語"),
        new("IT",      "Italian 義大利語"),
        new("LT",      "Lithuanian 立陶宛語"),
        new("LV",      "Latvian 拉脫維亞語"),
        new("NB",      "Norwegian 挪威語"),
        new("NL",      "Dutch 荷蘭語"),
        new("PL",      "Polish 波蘭語"),
        new("PT",      "Portuguese 葡萄牙語"),
        new("RO",      "Romanian 羅馬尼亞語"),
        new("RU",      "Russian 俄語"),
        new("SK",      "Slovak 斯洛伐克語"),
        new("SL",      "Slovenian 斯洛文尼亞語"),
        new("SV",      "Swedish 瑞典語"),
        new("TR",      "Turkish 土耳其語"),
        new("UK",      "Ukrainian 烏克蘭語"),
    ];

    public static readonly List<ProviderItem> Providers =
    [
        new(TranslationProvider.Google,  "Google 翻譯 (舊版)", false, "舊版 API"),
        new(TranslationProvider.Google2, "Google 翻譯", false, "新版 RPC API，穩定性較佳"),
        new(TranslationProvider.Bing,    "Bing 翻譯", false),
        new(TranslationProvider.Yandex,  "Yandex 翻譯", false, "Yandex 不支持繁體中文翻譯，使用繁體時會自動轉換為簡體中文"),
        new(TranslationProvider.DeepL,   "DeepL 翻譯", true, "需 API Key，可於 DeepL 官方申請（提供免費與付費方案）"),
    ];

    public static readonly List<OcrEngineItem> OcrEngines =
    [
        new(OcrEngineType.WindowsOcr, "Windows OCR",    "需在 Windows 語言設定中安裝對應語言包，未安裝對應語言時可能無法辨識或結果不完整"),
        new(OcrEngineType.Tesseract,  "Tesseract OCR",  "已內建中簡・中繁・日・韓・英等語言模型"),
    ];

    public static readonly List<LangItem> TargetLanguages =
    [
        new("ZH-HANT", "Chinese Traditional 繁體中文"),
        new("ZH-HANS", "Chinese Simplified 簡體中文"),
        new("EN-US",   "English 英語"),
        new("JA",      "Japanese 日語"),
        new("KO",      "Korean 韓語"),
        new("BG",      "Bulgarian 保加利亞語"),
        new("CS",      "Czech 捷克語"),
        new("DA",      "Danish 丹麥語"),
        new("DE",      "German 德語"),
        new("EL",      "Greek 希臘語"),
        new("ES",      "Spanish 西班牙語"),
        new("ET",      "Estonian 愛沙尼亞語"),
        new("FI",      "Finnish 芬蘭語"),
        new("FR",      "French 法語"),
        new("HU",      "Hungarian 匈牙利語"),
        new("ID",      "Indonesian 印尼語"),
        new("IT",      "Italian 義大利語"),
        new("LT",      "Lithuanian 立陶宛語"),
        new("LV",      "Latvian 拉脫維亞語"),
        new("NB",      "Norwegian 挪威語"),
        new("NL",      "Dutch 荷蘭語"),
        new("PL",      "Polish 波蘭語"),
        new("PT-BR",   "Portuguese 葡萄牙語"),
        new("RO",      "Romanian 羅馬尼亞語"),
        new("RU",      "Russian 俄語"),
        new("SK",      "Slovak 斯洛伐克語"),
        new("SL",      "Slovenian 斯洛文尼亞語"),
        new("SV",      "Swedish 瑞典語"),
        new("TR",      "Turkish 土耳其語"),
        new("UK",      "Ukrainian 烏克蘭語"),
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
}
