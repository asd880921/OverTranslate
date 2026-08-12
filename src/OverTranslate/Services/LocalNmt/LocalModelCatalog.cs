namespace OverTranslate.Services.LocalNmt;

public sealed record LocalModelDescriptor(
    string ModelId,
    string Version,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<LocalModelArtifact> Artifacts);

public enum LocalModelArtifactRole { Model, Vocabulary, LexicalShortlist }

public sealed record LocalModelArtifact(
    LocalModelArtifactRole Role,
    Uri DownloadUri,
    string FileName,
    long UncompressedSize,
    string UncompressedSha256);

public sealed record LocalTranslationRoute(
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<LocalModelDescriptor> Models)
{
    public bool IsPivot => Models.Count > 1;
    public string RouteId =>
        $"{SourceLanguage.ToLowerInvariant()}-{TargetLanguage.ToLowerInvariant()}:" +
        string.Join("+", Models.Select(model => model.ModelId));
}

/// <summary>The deliberately small, Phase 2 catalog of model directions validated by issue #47.</summary>
public sealed class LocalModelCatalog
{
    public const string CatalogVersion = "issue-47-hy-mt2-v1";

    private static readonly LocalModelDescriptor HyMt2 = new(
        "hy-mt2-1.8b-q4-k-m", "1cd5208700ac", "MULTI", "MULTI",
        [
            new LocalModelArtifact(
                LocalModelArtifactRole.Model,
                new Uri("https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/1cd5208700acedef4ef93019b6cfc148b8522d45/Hy-MT2-1.8B-Q4_K_M.gguf"),
                "Hy-MT2-1.8B-Q4_K_M.gguf",
                1133080448,
                "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699"),
        ]);

    public IReadOnlyList<LocalModelDescriptor> Models { get; } =
    [HyMt2];

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
            ("EN", "ZH-HANT") or
            ("JA", "ZH-HANT") or
            ("KO", "ZH-HANT") or
            ("ZH-HANT", "EN") => new(source, target, [HyMt2]),
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
