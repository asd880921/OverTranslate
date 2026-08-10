using OverTranslate.Models;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class LanguageDataTests
{
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
