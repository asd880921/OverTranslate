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

        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.Realtime.Provider);

        // English rather than 自動, which the realtime picker does not offer. This was empty on
        // purpose once — a blank asks the question instead of answering it badly — and stopped being
        // so when the shortcut arrived, because a shortcut has no page on which to ask. What it
        // must never be is 自動; see LanguageData.GetValidRealtimeSourceCode.
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, settings.Realtime.SourceLanguage);
        Assert.False(LanguageData.IsAutomaticSource(settings.Realtime.SourceLanguage));
        Assert.Equal("JA", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
    }

    [Fact]
    public void RealtimeTranslationSettings_DoNotChangeGeneralTranslationSettings()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"TargetLanguage":"KO","Provider":"OpenAI"}}""");

        Assert.Equal("KO", settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.Realtime.Provider);
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.TargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
    }

    [Fact]
    public void TheRealtimeSourceLanguageIsItsOwnKey()
    {
        // 即時翻譯 and 截圖翻譯 read different things at different times, so what one was last pointed
        // at says nothing about the other.
        var settings = SettingsService.Parse(
            """{"Realtime":{"SourceLanguage":"JA"},"SourceLanguage":"EN"}""");

        Assert.Equal("JA", settings.Realtime.SourceLanguage);
        Assert.Equal("EN", settings.SourceLanguage);
    }

    [Fact]
    public void AFileFromBeforeTheOpacityKey_KeepsTheBandItAlwaysHad()
    {
        // Every build before this one drew the scrim at a fixed alpha, so a settings file carried
        // across must not arrive at a different-looking overlay.
        var settings = SettingsService.Parse("""{"Realtime":{"ScrimColor":"#1E3A5F"}}""");

        Assert.Equal(
            OverTranslate.Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity,
            settings.Realtime.ScrimOpacity);
    }

    [Fact]
    public void TheOpacityIsItsOwnKey_AndDoesNotDisturbTheScrimColour()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"ScrimColor":"#1E3A5F","ScrimOpacity":0}}""");

        Assert.Equal(0, settings.Realtime.ScrimOpacity);
        Assert.Equal("#1E3A5F", settings.Realtime.ScrimColor);
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
            ApiKey = "round-trip",
            OpenAiBaseUrl = "http://localhost:1234/v1",
            OpenAiApiKey = "local-key",
            OpenAiModel = "local-model",
            Theme = "Light",
            AutoTranslateAfterSelection = true,
            SaveScreenshotToDisk = true,
            ScreenshotSavePath = @"D:\shots",
            TranslationWindowHotkeyEnabled = false,
            RealtimeHotkeyModifiers = 5,
            RealtimeHotkeyVirtualKey = 0x44,
            RealtimeHotkeyDisplay = "Ctrl+Shift+D",
            RealtimeHotkeyEnabled = false,
            Realtime =
            {
                BlockCount = 3,
                GuidanceExpanded = false,
                TargetLanguage = "JA",
                Provider = TranslationProvider.OpenAI,
                CaptureMode = RealtimeCaptureMode.Window,
                CaptureScreenDeviceName = @"\\.\DISPLAY2",
                CaptureWindowProcess = "chrome",
                CaptureWindowTitle = "Something - YouTube",
                NaturalBackgroundEnabled = true,
                SampleSourceTextColor = true,
            },
        });

        var settings = SettingsService.Parse(written);

        Assert.Equal(6u, settings.HotkeyModifiers);
        Assert.Equal(0x42u, settings.HotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+B", settings.HotkeyDisplay);
        Assert.Equal("KO", settings.SourceLanguage);
        Assert.Equal("EN", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
        Assert.Equal("JA", settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.Realtime.Provider);
        Assert.Equal("round-trip", settings.ApiKey);
        Assert.Equal("http://localhost:1234/v1", settings.OpenAiBaseUrl);
        Assert.Equal("local-key", settings.OpenAiApiKey);
        Assert.Equal("local-model", settings.OpenAiModel);
        Assert.Equal("Light", settings.Theme);
        Assert.True(settings.AutoTranslateAfterSelection);
        Assert.True(settings.SaveScreenshotToDisk);
        Assert.Equal(@"D:\shots", settings.ScreenshotSavePath);
        Assert.False(settings.Realtime.GuidanceExpanded);
        Assert.False(settings.TranslationWindowHotkeyEnabled);
        Assert.Equal(5u, settings.RealtimeHotkeyModifiers);
        Assert.Equal(0x44u, settings.RealtimeHotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimeHotkeyDisplay);
        Assert.False(settings.RealtimeHotkeyEnabled);
        Assert.Equal(3, settings.Realtime.BlockCount);
        Assert.Equal(RealtimeCaptureMode.Window, settings.Realtime.CaptureMode);
        Assert.Equal(@"\\.\DISPLAY2", settings.Realtime.CaptureScreenDeviceName);
        Assert.Equal("chrome", settings.Realtime.CaptureWindowProcess);
        Assert.Equal("Something - YouTube", settings.Realtime.CaptureWindowTitle);
        Assert.True(settings.Realtime.NaturalBackgroundEnabled);
        Assert.True(settings.Realtime.SampleSourceTextColor);
    }

    [Fact]
    public void OneUnreadableValueInAGroupCostsOnlyThatValue()
    {
        // The reason the reader descends into groups instead of deserialising them whole: handing a
        // group to the serialiser makes the group the unit that fails, so one hand-edited nonsense
        // capture mode would take the block count and the switches down with it.
        var settings = SettingsService.Parse(
            "{\n"
            + "  \"Realtime\": {\n"
            + "    \"CaptureMode\": \"Telepathy\",\n"
            + "    \"BlockCount\": 3,\n"
            + "    \"NaturalBackgroundEnabled\": true\n"
            + "  }\n"
            + "}");

        Assert.Equal(RealtimeCaptureMode.Screen, settings.Realtime.CaptureMode);
        Assert.Equal(3, settings.Realtime.BlockCount);
        Assert.True(settings.Realtime.NaturalBackgroundEnabled);
    }

    [Fact]
    public void AFileWrittenBeforeTheGroupExistedKeepsEverythingElse()
    {
        // What an upgrading user's file looks like: no Realtime object at all, and everything that
        // moved into it still written flat. Those values are gone — that was the trade, taken
        // knowing 即時翻譯's own page sets all of them again in one visit — but nothing outside the
        // group may go with them.
        var settings = SettingsService.Parse(
            "{\n"
            + "  \"RealtimeBlockCount\": 3,\n"
            + "  \"RealtimeTargetLanguage\": \"JA\",\n"
            + "  \"RealtimeScrimOpacity\": 12,\n"
            + "  \"RealtimeHotkeyDisplay\": \"Ctrl+Shift+D\",\n"
            + "  \"ApiKey\": \"kept\"\n"
            + "}");

        // Left where they were, so they still have to survive the move happening around them.
        Assert.Equal("kept", settings.ApiKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimeHotkeyDisplay);

        // Moved, so a file written before the move no longer says anything about them.
        Assert.Equal(1, settings.Realtime.BlockCount);
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.Realtime.TargetLanguage);
        Assert.Equal(
            OverTranslate.Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity,
            settings.Realtime.ScrimOpacity);
    }

    [Fact]
    public void GroupedSettingsAreWrittenAfterEveryFlatOne()
    {
        // The file is meant to read as two halves: everything that shipped before grouping
        // existed, then everything grouped. Properties are written in declaration order, so that
        // holds only by where the group is declared — exactly the kind of thing a later edit moves
        // without noticing, and the reader of appsettings.json is who pays.
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        var lastFlatKey = json.IndexOf("\"SkippedUpdateVersion\"", StringComparison.Ordinal);
        var group = json.IndexOf("\"Realtime\":", StringComparison.Ordinal);

        Assert.True(lastFlatKey >= 0 && group >= 0);
        Assert.True(group > lastFlatKey, "grouped settings must be written after every flat one");
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

        Assert.True(settings.Realtime.GuidanceExpanded);
    }
}
