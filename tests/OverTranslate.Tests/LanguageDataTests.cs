using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class LanguageDataTests
{
    [Fact]
    public void OpenAiCompatibleProvider_IsAvailableWithoutRequiringAKey()
    {
        var provider = LanguageData.Providers.Single(item =>
            item.Provider == TranslationProvider.OpenAI);

        // The wording moved to the string dictionaries when the interface gained a second language,
        // so what is asserted here is the wiring; StringsParityTests covers what the text says.
        Assert.Equal("S.Provider.OpenAI", provider.DisplayKey);
        Assert.False(provider.RequiresApiKey);
        Assert.Equal("S.Provider.OpenAIHint", provider.HintKey);
    }

    [Fact]
    public void PrimaryLanguages_UseConsistentOrder()
    {
        Assert.Equal(
            ["AUTO", "EN", "JA", "KO", "ZH", "ZH-HANT"],
            LanguageData.SourceLanguages.Take(6).Select(language => language.Code));
        Assert.Equal(
            ["AUTO", "EN", "JA", "KO", "ZH", "ZH-HANT"],
            LanguageData.OcrSourceLanguages.Take(6).Select(language => language.Code));
        Assert.Equal(
            ["EN-US", "JA", "KO", "ZH-HANS", "ZH-HANT"],
            LanguageData.TargetLanguages.Take(5).Select(language => language.Code));
    }

    [Fact]
    public void AutomaticSource_IsAvailableWithSupportedLanguagesInLabel()
    {
        var ocr = LanguageData.OcrSourceLanguages.Single(language =>
            language.Code == LanguageData.AutomaticSourceLanguage);

        Assert.Equal("自動（中英日）", ocr.Name);
        Assert.Contains(LanguageData.SourceLanguages, language =>
            language.Code == LanguageData.AutomaticSourceLanguage);
        Assert.Equal("AUTO", LanguageData.GetValidOcrSourceCode("auto"));
        Assert.Equal("AUTO", LanguageData.GetValidSourceCode("auto"));
    }

    /// <summary>
    /// The realtime control bar's language pair follows the interface language; the names inside
    /// the OpenAI prompt do not.
    /// </summary>
    /// <remarks>
    /// One accessor used to serve both, which is how "英語 → 繁體中文" ended up on an English
    /// control bar. Splitting them is the fix, and this is the line that has to stay split: the
    /// prompt around those names is written in Chinese, and swapping only the names to English
    /// because the user changed their buttons would alter what the model is asked to do.
    /// </remarks>
    [Theory]
    [InlineData("zh-Hant", "英語", "繁體中文")]
    [InlineData("en", "English", "Traditional Chinese")]
    public void DisplayNames_FollowTheInterfaceLanguage_ButPromptNamesDoNot(
        string uiLanguage, string expectedSource, string expectedTarget)
    {
        var settings = SettingsService.Instance.Current;
        var original = settings.UiLanguage;
        try
        {
            settings.UiLanguage = uiLanguage;

            Assert.Equal(expectedSource, LanguageData.GetSourceDisplayName("EN"));
            Assert.Equal(expectedTarget, LanguageData.GetTargetDisplayName("ZH-HANT"));

            // The prompt's names stay put whichever language the interface is in.
            Assert.Equal("英語", LanguageData.GetSourceName("EN"));
            Assert.Equal("繁體中文", LanguageData.GetTargetName("ZH-HANT"));
        }
        finally
        {
            settings.UiLanguage = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void TextSourceDefaultsToAutomatic(string? code)
    {
        Assert.Equal(LanguageData.AutomaticSourceLanguage, LanguageData.GetValidSourceCode(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void OcrSourceDefaultsToAutomatic(string? code)
    {
        Assert.Equal(LanguageData.AutomaticSourceLanguage, LanguageData.GetValidOcrSourceCode(code));
    }

    [Theory]
    [InlineData("AUTO", true)]
    [InlineData("auto", true)]
    [InlineData("EN", false)]
    [InlineData(null, false)]
    public void AutomaticSourceCheck_IsCaseInsensitive(string? code, bool expected)
    {
        Assert.Equal(expected, LanguageData.IsAutomaticSource(code));
    }

    [Fact]
    public void AutomaticSource_CannotBecomeATargetWhenLanguagesAreSwapped()
    {
        Assert.Null(LanguageData.MapSourceToTargetCode(LanguageData.AutomaticSourceLanguage));
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("auto")]
    public void AutomaticSource_IsOmittedFromTranslationProviderRequests(string code)
    {
        Assert.Null(GTranslateProvider.MapSourceToGTranslate(code));
        Assert.False(DeepLProvider.ShouldSendSourceLanguage(code));
    }

    [Fact]
    public void ManualSource_IsStillSentToTranslationProviderRequests()
    {
        Assert.Equal("en", GTranslateProvider.MapSourceToGTranslate("EN"));
        Assert.True(DeepLProvider.ShouldSendSourceLanguage("EN"));
    }
}
