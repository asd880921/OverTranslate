using OverTranslate.Models;

namespace OverTranslate.Services;

internal sealed record DictionaryLookupStep(
    TranslationProvider Provider,
    string TargetLanguage,
    bool ConvertToTraditional);

internal static class DictionaryLookupPlan
{
    internal static IReadOnlyList<DictionaryLookupStep> Build(
        TranslationProvider selectedProvider, string targetLanguage)
    {
        if (targetLanguage.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase))
            return BuildTraditionalChinese(selectedProvider, targetLanguage);

        return selectedProvider switch
        {
            TranslationProvider.Google =>
            [
                Native(TranslationProvider.Google, targetLanguage),
                Native(TranslationProvider.Microsoft, targetLanguage),
                Native(TranslationProvider.Bing, targetLanguage),
            ],
            TranslationProvider.Google2 =>
            [
                Native(TranslationProvider.Google, targetLanguage),
                Native(TranslationProvider.Microsoft, targetLanguage),
            ],
            TranslationProvider.Microsoft =>
            [
                Native(TranslationProvider.Microsoft, targetLanguage),
                Native(TranslationProvider.Google, targetLanguage),
                Native(TranslationProvider.Bing, targetLanguage),
            ],
            TranslationProvider.Bing =>
            [
                Native(TranslationProvider.Bing, targetLanguage),
                Native(TranslationProvider.Google, targetLanguage),
                Native(TranslationProvider.Microsoft, targetLanguage),
            ],
            TranslationProvider.DeepL =>
            [
                Native(TranslationProvider.Google, targetLanguage),
                Native(TranslationProvider.Microsoft, targetLanguage),
            ],
            _ => [],
        };
    }

    private static IReadOnlyList<DictionaryLookupStep> BuildTraditionalChinese(
        TranslationProvider selectedProvider, string targetLanguage) => selectedProvider switch
    {
        TranslationProvider.Google =>
        [
            Native(TranslationProvider.Google, targetLanguage),
            Converted(TranslationProvider.Microsoft),
            Converted(TranslationProvider.Bing),
        ],
        TranslationProvider.Google2 =>
        [
            Native(TranslationProvider.Google, targetLanguage),
            Converted(TranslationProvider.Microsoft),
        ],
        TranslationProvider.Microsoft =>
        [
            Converted(TranslationProvider.Microsoft),
            Native(TranslationProvider.Google, targetLanguage),
            Converted(TranslationProvider.Bing),
        ],
        TranslationProvider.Bing =>
        [
            Converted(TranslationProvider.Bing),
            Native(TranslationProvider.Google, targetLanguage),
            Converted(TranslationProvider.Microsoft),
        ],
        TranslationProvider.DeepL =>
        [
            Native(TranslationProvider.Google, targetLanguage),
            Converted(TranslationProvider.Microsoft),
        ],
        _ => [],
    };

    private static DictionaryLookupStep Native(
        TranslationProvider provider, string targetLanguage) =>
        new(provider, targetLanguage, ConvertToTraditional: false);

    private static DictionaryLookupStep Converted(TranslationProvider provider) =>
        new(provider, "ZH-HANS", ConvertToTraditional: true);
}
