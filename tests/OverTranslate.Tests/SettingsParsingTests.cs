using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// Settings are read back from a file the user may have carried across several versions, so the
// parser meets shapes the current build never wrote: keys added since, keys since removed, enum
// values retired, a file truncated by a power cut. The old parser answered all of those the same
// way — throw away everything and start from defaults — which silently cost people their API key
// and hotkey. These tests pin the rule that only the unreadable field pays.
public class SettingsParsingTests
{
    [Fact]
    public void MissingOpenAiSettings_UseSafeDefaults()
    {
        var settings = SettingsService.Parse("{}");

        Assert.Equal("https://api.openai.com/v1", settings.OpenAiBaseUrl);
        Assert.Equal("", settings.OpenAiApiKey);
        Assert.Equal("", settings.OpenAiModel);
    }

    [Fact]
    public void MissingRealtimeTranslationSettings_UseIndependentDefaults()
    {
        var settings = SettingsService.Parse(
            """{"TargetLanguage":"JA","Provider":"DeepL"}""");

        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.RealtimeTargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.RealtimeProvider);
        Assert.Equal("JA", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
    }

    [Fact]
    public void RealtimeTranslationSettings_DoNotChangeGeneralTranslationSettings()
    {
        var settings = SettingsService.Parse(
            """{"RealtimeTargetLanguage":"KO","RealtimeProvider":"OpenAI"}""");

        Assert.Equal("KO", settings.RealtimeTargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.RealtimeProvider);
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.TargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
    }

    [Fact]
    public void MissingKeys_KeepTheirDefaults_AndLeaveTheRestIntact()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","SourceLanguage":"JA","ApiKey":"secret"}""");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("JA", settings.SourceLanguage);
        Assert.Equal("secret", settings.ApiKey);

        Assert.Equal("ZH-HANT", settings.TargetLanguage);
        Assert.Equal("Ctrl+Alt+A", settings.HotkeyDisplay);
        Assert.False(settings.AutoTranslateAfterSelection);
    }

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","RetiredIn160":123}""");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    // The reason the old catch was dangerous: dropping or renaming a TranslationProvider member
    // would have wiped every setting of everyone still storing that value.
    [Fact]
    public void UnknownEnumValue_CostsOnlyThatField()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","Provider":"Papago"}""");

        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    [Fact]
    public void WrongType_CostsOnlyThatField()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","AutoTranslateAfterSelection":"yes"}""");

        Assert.False(settings.AutoTranslateAfterSelection);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    // Save never writes null, but a hand-edited file can hold one. It must not become a null string.
    [Fact]
    public void ExplicitNull_FallsBackToTheDefaultRatherThanNull()
    {
        var settings = SettingsService.Parse(
            """{"Theme":null,"ApiKey":null,"SourceLanguage":null}""");

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("", settings.ApiKey);
        Assert.Equal(LanguageData.AutomaticSourceLanguage, settings.SourceLanguage);
    }

    [Theory]
    [InlineData("""{"Theme":"Light","ApiKey":"my-sec""")]   // truncated mid-write
    [InlineData("")]                                        // zero-byte file
    [InlineData("not json at all")]
    public void UnparseableFile_FallsBackToDefaults(string json)
    {
        var settings = SettingsService.Parse(json);

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("", settings.ApiKey);
        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
    }

    [Fact]
    public void EmptyObject_YieldsDefaults()
    {
        var settings = SettingsService.Parse("{}");

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Ctrl+Alt+A", settings.HotkeyDisplay);
        Assert.Equal(LanguageData.AutomaticSourceLanguage, settings.SourceLanguage);
    }

    // A round trip has to survive, or the tolerant read would quietly drop values on every save.
    [Fact]
    public void EveryFieldSurvivesARoundTrip()
    {
        var written = System.Text.Json.JsonSerializer.Serialize(new AppSettings
        {
            HotkeyModifiers = 6,
            HotkeyVirtualKey = 0x42,
            HotkeyDisplay = "Ctrl+Shift+B",
            SourceLanguage = "KO",
            TargetLanguage = "EN",
            Provider = TranslationProvider.DeepL,
            RealtimeTargetLanguage = "JA",
            RealtimeProvider = TranslationProvider.OpenAI,
            ApiKey = "round-trip",
            OpenAiBaseUrl = "http://localhost:1234/v1",
            OpenAiApiKey = "local-key",
            OpenAiModel = "local-model",
            Theme = "Light",
            AutoTranslateAfterSelection = true,
            SaveScreenshotToDisk = true,
            ScreenshotSavePath = @"D:\shots",
        });

        var settings = SettingsService.Parse(written);

        Assert.Equal(6u, settings.HotkeyModifiers);
        Assert.Equal(0x42u, settings.HotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+B", settings.HotkeyDisplay);
        Assert.Equal("KO", settings.SourceLanguage);
        Assert.Equal("EN", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
        Assert.Equal("JA", settings.RealtimeTargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.RealtimeProvider);
        Assert.Equal("round-trip", settings.ApiKey);
        Assert.Equal("http://localhost:1234/v1", settings.OpenAiBaseUrl);
        Assert.Equal("local-key", settings.OpenAiApiKey);
        Assert.Equal("local-model", settings.OpenAiModel);
        Assert.Equal("Light", settings.Theme);
        Assert.True(settings.AutoTranslateAfterSelection);
        Assert.True(settings.SaveScreenshotToDisk);
        Assert.Equal(@"D:\shots", settings.ScreenshotSavePath);
    }
}
