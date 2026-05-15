namespace OverTranslate.Services.Ocr;

internal static class OcrLanguageRouter
{
    public static string Normalize(string code) =>
        string.IsNullOrWhiteSpace(code) ? "EN" : code.Trim().ToUpperInvariant();

    public static bool UsesEnglishTesseract(string code) => Normalize(code) == "EN";

    public static bool UsesCjkOnnx(string code) => Normalize(code) is "ZH" or "ZH-HANT" or "JA" or "KO";

    public static bool IsSupported(string code) =>
        UsesEnglishTesseract(code) || UsesCjkOnnx(code);

    public static string GetUnsupportedLanguageMessage(string code) =>
        $"目前 OCR 僅支援 EN、ZH、ZH-HANT、JA、KO；「{Normalize(code)}」尚未支援。";
}
