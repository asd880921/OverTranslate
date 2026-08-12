namespace OverTranslate.Services.LocalNmt;

/// <summary>Versioned identity that prevents stale local-model translations surviving upgrades.</summary>
public readonly record struct LocalTranslationCacheKey(
    string RuntimeId,
    string CatalogVersion,
    string RouteId,
    string ModelVersions,
    string SourceLanguage,
    string TargetLanguage,
    string NormalizationVersion,
    string Text)
{
    public static LocalTranslationCacheKey Create(
        LocalTranslationRoute route,
        string text,
        string normalizationVersion,
        string runtimeId = "bergamot") => new(
            runtimeId,
            LocalModelCatalog.CatalogVersion,
            route.RouteId,
            string.Join("+", route.Models.Select(model => model.Version)),
            route.SourceLanguage,
            route.TargetLanguage,
            normalizationVersion,
            text);
}
