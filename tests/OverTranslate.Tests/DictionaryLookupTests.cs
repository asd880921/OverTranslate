using System.Net.Http;
using System.Xml.Linq;
using GTranslate.Translators;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class DictionaryLookupTests
{
    [Theory]
    [InlineData(TranslationProvider.Google, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False,Bing:EN-US:False")]
    [InlineData(TranslationProvider.Google2, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.Microsoft, "EN-US", "Microsoft:EN-US:False,Google:EN-US:False,Bing:EN-US:False")]
    [InlineData(TranslationProvider.Bing, "EN-US", "Bing:EN-US:False,Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.DeepL, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.Google, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True,Bing:ZH-HANS:True")]
    [InlineData(TranslationProvider.Google2, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.Microsoft, "ZH-HANT", "Microsoft:ZH-HANS:True,Google:ZH-HANT:False,Bing:ZH-HANS:True")]
    [InlineData(TranslationProvider.Bing, "ZH-HANT", "Bing:ZH-HANS:True,Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.DeepL, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.OpenAI, "ZH-HANT", "")]
    public void Dictionary_fallback_plan_matches_the_selected_provider_and_target(
        TranslationProvider provider, string targetLanguage, string expected)
    {
        var actual = string.Join(",", DictionaryLookupPlan.Build(provider, targetLanguage)
            .Select(step => $"{step.Provider}:{step.TargetLanguage}:{step.ConvertToTraditional}"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Simplified_dictionary_results_are_converted_without_exposing_the_conversion()
    {
        var source = new DictionaryLookupData(
            "cost", "Microsoft", "软件", null,
            [new DictionaryLookupGroupData("noun", [
                new DictionaryEntryData("多个翻译", null, null, null, [], ["简体中文"], ["词性"], [
                    new DictionaryExampleData("source", "这个翻译")
                ])
            ], ["多个定义"], ["同义词"])], []);

        var result = DictionaryTraditionalChineseConverter.Convert(source);

        Assert.Equal("Microsoft", result.Service);
        Assert.Equal("軟體", result.Headword);
        Assert.Equal("多個翻譯", result.Groups[0].Entries[0].Text);
        Assert.Equal("簡體中文", result.Groups[0].Entries[0].Definitions[0]);
        Assert.Equal("詞性", result.Groups[0].Entries[0].Synonyms[0]);
        Assert.Equal("這個翻譯", result.Groups[0].Entries[0].Examples[0].Translation);
        Assert.Equal("多個定義", result.Groups[0].Definitions[0]);
        Assert.Equal("同義詞", result.Groups[0].Synonyms[0]);
    }

    [Fact]
    public void Dictionary_results_expose_only_groups_with_a_part_of_speech()
    {
        var unlabelled = new DictionaryLookupGroupData(null, [
            new DictionaryEntryData("價錢為", null, null, null, [], [], [], [])
        ], [], []);
        var noun = new DictionaryLookupGroupData("noun", [
            new DictionaryEntryData("成本", null, null, null, [], [], [], [])
        ], [], []);
        var result = new DictionaryLookupData("cost", "Google Web", "cost", null, [unlabelled, noun], []);

        Assert.Equal([noun], result.DisplayGroups);
        Assert.True(result.HasContent);
        Assert.False((result with { Groups = [unlabelled] }).HasContent);
    }

    [Fact]
    public void Dictionary_view_renders_only_concise_provider_details()
    {
        var path = Path.Combine(
            StringsParityTests.ProjectDirectory(),
            "Views", "Controls", "DictionaryResultView.xaml");

        var document = XDocument.Load(path);
        var bindings = document
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToList();

        Assert.Contains("{Binding BackTranslationsText}", bindings);
        Assert.DoesNotContain("{Binding Examples}", bindings);
        Assert.DoesNotContain("{Binding DefinitionsText}", bindings);
        Assert.DoesNotContain("{Binding SynonymsText}", bindings);
        Assert.Empty(document.Descendants("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}ProgressBar"));
    }

    [Fact]
    public void Dictionary_loading_bar_is_scoped_to_text_translation()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var projectDirectory = StringsParityTests.ProjectDirectory();
        var translation = XDocument.Load(Path.Combine(
            projectDirectory, "Views", "Translation", "TranslationPage.xaml"));
        var quickLookup = XDocument.Load(Path.Combine(
            projectDirectory, "Views", "QuickLookup", "QuickLookupWindow.xaml"));

        var bar = translation.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "DictionaryLoadingBar");

        Assert.Equal("2", bar.Attribute("Height")?.Value);
        Assert.Equal("True", bar.Attribute("IsIndeterminate")?.Value);
        Assert.DoesNotContain(quickLookup.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "DictionaryLoadingBar");
    }

    [Fact]
    public async Task Dictionary_lookup_falls_back_when_the_selected_provider_rejects_the_language_pair()
    {
        var expected = new DictionaryLookupData(
            "cost", "Google Web", "cost", null,
            [new DictionaryLookupGroupData("noun", [
                new DictionaryEntryData("成本", null, null, null, [], [], [], [])
            ], [], [])], []);
        var attempts = 0;

        var result = await DictionaryLookupFallback.TryAsync([
            _ =>
            {
                attempts++;
                return Task.FromException<DictionaryLookupData?>(
                    new HttpRequestException("The API returned status code 400."));
            },
            _ =>
            {
                attempts++;
                return Task.FromResult<DictionaryLookupData?>(expected);
            }
        ]);

        Assert.Same(expected, result);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("charge")]
    [InlineData("credit card")]
    [InlineData("look forward to")]
    [InlineData("state-of-the-art")]
    [InlineData("don't")]
    [InlineData("New　York")]
    [InlineData("銀行")]
    [InlineData("飛ぶ")]
    public void Words_and_short_phrases_are_dictionary_candidates(string text)
    {
        Assert.True(DictionaryLookupEligibility.IsEligible(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("one two three four five")]
    [InlineData("one two three four five six seven")]
    [InlineData("first line\nsecond line")]
    [InlineData("cost.")]
    [InlineData("hello, world")]
    [InlineData("這是一段沒有空白而且超過十六個中文字的完整句子")]
    [InlineData("This deliberately long input exceeds the dictionary candidate character limit by a lot.")]
    public void Empty_or_sentence_like_text_skips_the_extra_request(string text)
    {
        Assert.False(DictionaryLookupEligibility.IsEligible(text));
    }

    [Fact]
    public void Provider_capabilities_decide_whether_dictionary_lookup_is_offered()
    {
        using var http = new HttpClient();

        Assert.True(new GTranslateProvider(new GoogleTranslator(http)).SupportsDictionary);
        Assert.True(new GTranslateProvider(new BingTranslator(http)).SupportsDictionary);
        Assert.True(new GTranslateProvider(new MicrosoftTranslator(http)).SupportsDictionary);
        Assert.False(new GTranslateProvider(new GoogleTranslator2(http)).SupportsDictionary);
    }

    [Theory]
    [InlineData("Views/Translation/TranslationPage.xaml")]
    [InlineData("Views/QuickLookup/QuickLookupWindow.xaml")]
    public void Rich_results_share_the_same_dictionary_view(string relativePath)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var path = Path.Combine(
            StringsParityTests.ProjectDirectory(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        var dictionary = XDocument.Load(path)
            .Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "DictionaryView");

        Assert.Equal("DictionaryResultView", dictionary.Name.LocalName);
    }
}
