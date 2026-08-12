namespace OverTranslate.Services.LocalNmt;

public sealed record LocalModelDescriptor(
    string ModelId,
    string Version,
    string SourceLanguage,
    string TargetLanguage);

public sealed record LocalTranslationRoute(
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<LocalModelDescriptor> Models)
{
    public bool IsPivot => Models.Count > 1;
    public string RouteId => string.Join("+", Models.Select(model => model.ModelId));
}

/// <summary>The deliberately small, Phase 2 catalog of model directions validated by issue #47.</summary>
public sealed class LocalModelCatalog
{
    public const string CatalogVersion = "issue-47-v1";

    private static readonly LocalModelDescriptor EnglishToTraditionalChinese = new(
        "bergamot-en-zh-hant", "559ab90d723a", "EN", "ZH-HANT");

    private static readonly LocalModelDescriptor JapaneseToEnglish = new(
        "bergamot-ja-en", "a9bf800679bb", "JA", "EN");

    private static readonly LocalModelDescriptor KoreanToEnglish = new(
        "bergamot-ko-en", "1c902d6f7a8d", "KO", "EN");

    private static readonly LocalModelDescriptor TraditionalChineseToEnglish = new(
        "bergamot-zh-hant-en", "0aee91790894", "ZH-HANT", "EN");

    public IReadOnlyList<LocalModelDescriptor> Models { get; } =
    [
        EnglishToTraditionalChinese,
        JapaneseToEnglish,
        KoreanToEnglish,
        TraditionalChineseToEnglish,
    ];

    public bool TryResolve(
        string sourceLanguage,
        string targetLanguage,
        out LocalTranslationRoute? route,
        out string? diagnostic)
    {
        var source = NormalizeSource(sourceLanguage);
        var target = NormalizeTarget(targetLanguage);

        if (source == "AUTO")
        {
            route = null;
            diagnostic = "Local translation requires a resolved source language; AUTO cannot select a model safely.";
            return false;
        }

        route = (source, target) switch
        {
            ("EN", "ZH-HANT") => new(source, target, [EnglishToTraditionalChinese]),
            ("JA", "ZH-HANT") => new(source, target, [JapaneseToEnglish, EnglishToTraditionalChinese]),
            ("KO", "ZH-HANT") => new(source, target, [KoreanToEnglish, EnglishToTraditionalChinese]),
            ("ZH-HANT", "EN") => new(source, target, [TraditionalChineseToEnglish]),
            _ => null,
        };

        diagnostic = route is null
            ? $"Local translation does not support {sourceLanguage} to {targetLanguage}."
            : null;
        return route is not null;
    }

    public LocalTranslationRoute Resolve(string sourceLanguage, string targetLanguage) =>
        TryResolve(sourceLanguage, targetLanguage, out var route, out var diagnostic)
            ? route!
            : throw new NotSupportedException(diagnostic);

    private static string NormalizeSource(string language) => language.Trim().ToUpperInvariant() switch
    {
        "EN-US" or "EN-GB" => "EN",
        "ZH-TW" => "ZH-HANT",
        var normalized => normalized,
    };

    private static string NormalizeTarget(string language) => language.Trim().ToUpperInvariant() switch
    {
        "EN-US" or "EN-GB" => "EN",
        "ZH-TW" => "ZH-HANT",
        var normalized => normalized,
    };
}
