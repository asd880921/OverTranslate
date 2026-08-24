using OpenccNetLib;
using OverTranslate.Models;

namespace OverTranslate.Services;

internal static class DictionaryTraditionalChineseConverter
{
    private static readonly Opencc Converter = new(OpenccConfig.S2Twp);

    internal static DictionaryLookupData Convert(DictionaryLookupData source) =>
        source with
        {
            Source = ConvertText(source.Source),
            Headword = ConvertOptional(source.Headword),
            Groups = source.Groups.Select(group => group with
            {
                Entries = group.Entries.Select(entry => entry with
                {
                    Text = ConvertText(entry.Text),
                    BackTranslations = ConvertAll(entry.BackTranslations),
                    Definitions = ConvertAll(entry.Definitions),
                    Synonyms = ConvertAll(entry.Synonyms),
                    Examples = ConvertExamples(entry.Examples),
                }).ToList(),
                Definitions = ConvertAll(group.Definitions),
                Synonyms = ConvertAll(group.Synonyms),
            }).ToList(),
            Examples = ConvertExamples(source.Examples),
        };

    private static IReadOnlyList<string> ConvertAll(IReadOnlyList<string> values) =>
        values.Select(ConvertText).ToList();

    private static IReadOnlyList<DictionaryExampleData> ConvertExamples(
        IReadOnlyList<DictionaryExampleData> examples) =>
        examples.Select(example => example with
        {
            Source = ConvertText(example.Source),
            Translation = ConvertOptional(example.Translation),
        }).ToList();

    private static string? ConvertOptional(string? value) =>
        string.IsNullOrEmpty(value) ? value : ConvertText(value);

    private static string ConvertText(string value) =>
        value.Length == 0 ? value : Converter.Convert(value, punctuation: false);
}
