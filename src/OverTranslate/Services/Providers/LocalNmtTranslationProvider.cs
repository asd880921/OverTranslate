using System.IO;
using OverTranslate.Services.LocalNmt;

namespace OverTranslate.Services.Providers;

/// <summary>Adapts the local runtime to the provider contract shared by all translation features.</summary>
public sealed class LocalNmtTranslationProvider(
    ILocalTranslationRuntime runtime,
    LocalModelCatalog? catalog = null) : ITranslationProvider
{
    private readonly LocalModelCatalog _catalog = catalog ?? new LocalModelCatalog();

    public bool RequiresApiKey => false;

    public string GetCacheIdentity(
        string sourceLanguage,
        string targetLanguage,
        string normalizationVersion)
    {
        var route = _catalog.Resolve(sourceLanguage, targetLanguage);
        var versions = string.Join("+", route.Models.Select(model => model.Version));
        return $"bergamot|{LocalModelCatalog.CatalogVersion}|{route.RouteId}|{versions}|" +
               $"{route.SourceLanguage}|{route.TargetLanguage}|{normalizationVersion}";
    }

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var result = await runtime.TranslateAsync(
            new LocalTranslationRequest(
                blocks.Select(block => block.Text).ToArray(),
                sourceLang,
                targetLang),
            cancellationToken);

        if (result.Translations.Count != blocks.Count)
            throw new InvalidDataException(
                $"Local translation returned {result.Translations.Count} results for {blocks.Count} blocks.");

        var translated = blocks.Zip(result.Translations, (block, text) => new TranslatedBlock(
            block.Text,
            text,
            block.Bounds,
            block.Lines,
            block.SourceGlyphHeight)).ToList();
        return (translated, result.DetectedLanguage);
    }
}
