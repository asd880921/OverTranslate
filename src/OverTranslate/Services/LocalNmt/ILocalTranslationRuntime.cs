namespace OverTranslate.Services.LocalNmt;

public sealed record LocalTranslationRequest(
    IReadOnlyList<string> Texts,
    string SourceLanguage,
    string TargetLanguage);

public sealed record LocalTranslationResult(
    IReadOnlyList<string> Translations,
    string DetectedLanguage);

/// <summary>
/// Runtime boundary for local translation. Model discovery, loading and worker-process details
/// stay behind this interface instead of leaking into translation views or cloud providers.
/// </summary>
public interface ILocalTranslationRuntime
{
    Task<LocalTranslationResult> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default);
}
