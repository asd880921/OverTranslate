using System.Text.Json;

namespace OverTranslate.TranslationHarness;

public sealed record TranslationCorpus(
    int SchemaVersion,
    string CorpusId,
    string CorpusVersion,
    bool Deidentified,
    string? Notes,
    IReadOnlyList<TranslationCase> Cases);

public sealed record TranslationCase(
    string Id,
    string Category,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string ReferenceTranslation,
    IReadOnlyList<string>? Tags = null);

public static class CorpusLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<TranslationCorpus> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var corpus = await JsonSerializer.DeserializeAsync<TranslationCorpus>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Corpus JSON is empty.");

        Validate(corpus);
        return corpus;
    }

    public static void Validate(TranslationCorpus corpus)
    {
        var errors = new List<string>();

        if (corpus.SchemaVersion != 1)
            errors.Add($"schemaVersion must be 1, but was {corpus.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(corpus.CorpusId))
            errors.Add("corpusId is required.");
        if (string.IsNullOrWhiteSpace(corpus.CorpusVersion))
            errors.Add("corpusVersion is required.");
        if (!corpus.Deidentified)
            errors.Add("deidentified must be true before a corpus can be replayed.");
        if (corpus.Cases is null || corpus.Cases.Count == 0)
            errors.Add("At least one case is required.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in corpus.Cases ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                errors.Add("Every case must have an id.");
            else if (!ids.Add(item.Id))
                errors.Add($"Duplicate case id: {item.Id}.");
            if (string.IsNullOrWhiteSpace(item.Category))
                errors.Add($"Case {item.Id} must have a category.");
            if (string.IsNullOrWhiteSpace(item.SourceLanguage))
                errors.Add($"Case {item.Id} must have a sourceLanguage.");
            if (string.IsNullOrWhiteSpace(item.TargetLanguage))
                errors.Add($"Case {item.Id} must have a targetLanguage.");
            if (string.IsNullOrWhiteSpace(item.SourceText))
                errors.Add($"Case {item.Id} must have sourceText.");
            if (string.IsNullOrWhiteSpace(item.ReferenceTranslation))
                errors.Add($"Case {item.Id} must have a human referenceTranslation.");
        }

        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
