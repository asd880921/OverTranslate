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

        // Empty rather than the addresses and names themselves: the provider fills those in, so a
        // settings file that never mentions them keeps following whatever the build defaults to.
        Assert.Equal("", settings.OpenAiBaseUrl);
        Assert.Equal("", settings.OpenAiApiKey);
        Assert.Equal("", settings.OpenAiModel);
        Assert.True(settings.OpenAiTemperatureEnabled);
        Assert.Equal(0, settings.OpenAiTemperature);
    }

    [Fact]
    public void MissingRealtimeTranslationSettings_UseIndependentDefaults()
    {
        var settings = SettingsService.Parse(
            """{"TargetLanguage":"JA","Provider":"DeepL"}""");

        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.RealtimeTargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.RealtimeProvider);

        // English rather than 自動, which the realtime picker does not offer. This was empty on
        // purpose once — a blank asks the question instead of answering it badly — and stopped being
        // so when the shortcut arrived, because a shortcut has no page on which to ask. What it
        // must never be is 自動; see LanguageData.GetValidRealtimeSourceCode.
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, settings.RealtimeSourceLanguage);
        Assert.False(LanguageData.IsAutomaticSource(settings.RealtimeSourceLanguage));
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
    public void TheRealtimeSourceLanguageIsItsOwnKey()
    {
        // 即時翻譯 and 截圖翻譯 read different things at different times, so what one was last pointed
        // at says nothing about the other.
        var settings = SettingsService.Parse(
            """{"RealtimeSourceLanguage":"JA","SourceLanguage":"EN"}""");

        Assert.Equal("JA", settings.RealtimeSourceLanguage);
        Assert.Equal("EN", settings.SourceLanguage);
    }

    [Fact]
    public void AFileFromBeforeTheOpacityKey_KeepsTheBandItAlwaysHad()
    {
        // Every build before this one drew the scrim at a fixed alpha, so a settings file carried
        // across must not arrive at a different-looking overlay.
        var settings = SettingsService.Parse("""{"RealtimeScrimColor":"#1E3A5F"}""");

        Assert.Equal(
            OverTranslate.Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity,
            settings.RealtimeScrimOpacity);
    }

    [Fact]
    public void TheOpacityIsItsOwnKey_AndDoesNotDisturbTheScrimColour()
    {
        var settings = SettingsService.Parse(
            """{"RealtimeScrimColor":"#1E3A5F","RealtimeScrimOpacity":0}""");

        Assert.Equal(0, settings.RealtimeScrimOpacity);
        Assert.Equal("#1E3A5F", settings.RealtimeScrimColor);
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
            RealtimeGuidanceExpanded = false,
            TranslationWindowHotkeyEnabled = false,
            RealtimeHotkeyModifiers = 5,
            RealtimeHotkeyVirtualKey = 0x44,
            RealtimeHotkeyDisplay = "Ctrl+Shift+D",
            RealtimeHotkeyEnabled = false,
            RealtimeBlockCount = 3,
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
        Assert.False(settings.RealtimeGuidanceExpanded);
        Assert.False(settings.TranslationWindowHotkeyEnabled);
        Assert.Equal(5u, settings.RealtimeHotkeyModifiers);
        Assert.Equal(0x44u, settings.RealtimeHotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimeHotkeyDisplay);
        Assert.False(settings.RealtimeHotkeyEnabled);
        Assert.Equal(3, settings.RealtimeBlockCount);
    }

    // Written before either shortcut could be switched off: both have to come back on, or upgrading
    // would silently disable two shortcuts the user still has.
    [Fact]
    public void MissingHotkeyEnabledSettings_StartOn()
    {
        var settings = SettingsService.Parse("""{"Theme":"Light"}""");

        Assert.True(settings.TranslationWindowHotkeyEnabled);
        Assert.True(settings.RealtimeHotkeyEnabled);
        Assert.Equal("Ctrl+Alt+S", settings.RealtimeHotkeyDisplay);
    }

    // Expanded on a file written before the setting existed: someone who has never been shown the
    // framing guidance must not have it folded away on their behalf.
    [Fact]
    public void MissingRealtimeGuidanceSetting_StartsExpanded()
    {
        var settings = SettingsService.Parse("""{"Theme":"Light"}""");

        Assert.True(settings.RealtimeGuidanceExpanded);
    }
}
