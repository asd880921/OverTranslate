namespace OverTranslate.Models;

public sealed record DictionaryLookupData(
    string Source,
    string Service,
    string? Headword,
    string? Pronunciation,
    IReadOnlyList<DictionaryLookupGroupData> Groups,
    IReadOnlyList<DictionaryExampleData> Examples)
{
    public IReadOnlyList<DictionaryLookupGroupData> DisplayGroups => Groups
        .Where(group => group.HasPartOfSpeech && group.Entries.Count > 0)
        .ToArray();
    public bool HasContent => DisplayGroups.Count > 0;
    public bool HasHeadword => !string.IsNullOrWhiteSpace(Headword);
    public bool HasPronunciation => !string.IsNullOrWhiteSpace(Pronunciation);
    public bool HasExamples => Examples.Count > 0;
}

public sealed record DictionaryLookupGroupData(
    string? PartOfSpeech,
    IReadOnlyList<DictionaryEntryData> Entries,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> Synonyms)
{
    public bool HasPartOfSpeech => !string.IsNullOrWhiteSpace(PartOfSpeech);
    public string PartOfSpeechLabel => string.IsNullOrWhiteSpace(PartOfSpeech)
        ? "—"
        : PartOfSpeech.ToUpperInvariant();
    public string DefinitionsText => string.Join(Environment.NewLine, Definitions);
    public string SynonymsText => string.Join(" · ", Synonyms);
}

public sealed record DictionaryEntryData(
    string Text,
    string? Transliteration,
    double? Confidence,
    long? Frequency,
    IReadOnlyList<string> BackTranslations,
    IReadOnlyList<DictionaryExampleData> Examples)
{
    public string BackTranslationsText => string.Join(" · ", BackTranslations);
    public bool HasTransliteration => !string.IsNullOrWhiteSpace(Transliteration);
}

public sealed record DictionaryExampleData(string Source, string? Translation)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Translation)
        ? Source
        : $"{Source}  →  {Translation}";
}
