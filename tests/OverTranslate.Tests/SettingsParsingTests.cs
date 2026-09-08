using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
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
    public void MissingCaptureSettings_UseHorizontalTextByDefault()
    {
        var settings = SettingsService.Parse("{}");

        Assert.False(settings.Capture.VerticalText);
    }

    [Fact]
    public void CaptureTextDirection_IsReadFromItsOwnGroup()
    {
        var settings = SettingsService.Parse(
            """{"Capture":{"VerticalText":true}}""");

        Assert.True(settings.Capture.VerticalText);
    }

    /// <summary>
    /// A name this build writes reads back as itself. design.md §15.5, the two cases that keep the
    /// user's answer.
    /// </summary>
    /// <remarks>
    /// The other four cases of that table — the two v1 names, an unknown one, and no key at all —
    /// are the two tests below. Between the three of them every value that can be sitting in a
    /// settings file has somewhere it lands.
    /// </remarks>
    [Theory]
    [InlineData("General", CaptureLayoutMode.General)]
    [InlineData("Interface", CaptureLayoutMode.Interface)]
    public void CaptureLayoutMode_IsReadFromItsOwnGroup(string stored, CaptureLayoutMode expected)
    {
        var settings = SettingsService.Parse(
            "{\"Capture\":{\"LayoutMode\":\"" + stored + "\"}}");

        Assert.Equal(expected, settings.Capture.LayoutMode);
    }

    /// <summary>
    /// Every settings file written before this switch existed, and every first run, opens on 一般.
    /// </summary>
    [Fact]
    public void ASettingsFileWithoutALayoutMode_OpensOnTheDefault()
    {
        var settings = SettingsService.Parse("""{"Capture":{"VerticalText":true}}""");

        Assert.Equal(CaptureLayoutMode.General, settings.Capture.LayoutMode);
        Assert.True(settings.Capture.VerticalText);
    }

    /// <summary>
    /// A mode name this build cannot read falls back to 一般, and takes nothing else with it.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole of design.md §15.5's fallback. It is not code of its own: the mode is
    /// stored by name, a name that is not a member will not deserialize, and the settings reader
    /// already keeps a property's default when a value will not read. The test is here to say the
    /// chain actually closes, because the day it stops closing is a day nobody looks.</para>
    ///
    /// <para>The v1 names go through the same door, which is why the theory carries them: a file
    /// still saying ComicArticle or Standard is not a hypothetical, it is on the disk of everyone
    /// who ran the previous build. Standard landing on 一般 rather than on 介面 is deliberate and
    /// is the one case worth arguing about — Standard was the old default, so somebody storing it
    /// most likely never opened the switch at all, and what that means is "no opinion", not
    /// "keep me conservative". No opinion gets the new default. The remaining pair of cases —
    /// General and Interface written by this build — round-trip below.</para>
    /// </remarks>
    [Theory]
    [InlineData("Webtoon")]      // a mode a later release named
    [InlineData("ComicArticle")] // v1: the mode that became General
    [InlineData("Standard")]     // v1: the old default
    public void ALayoutModeNameThisBuildCannotRead_OpensOnTheDefaultAndCostsNothingElse(string stored)
    {
        var settings = SettingsService.Parse(
            "{\"ApiKey\":\"secret\",\"Capture\":{\"LayoutMode\":\"" + stored + "\",\"VerticalText\":true}}");

        Assert.Equal(CaptureLayoutMode.General, settings.Capture.LayoutMode);
        Assert.True(settings.Capture.VerticalText);
        Assert.Equal("secret", settings.ApiKey);
    }

    /// <summary>
    /// Written as the name, not as a number or a flag.
    /// </summary>
    /// <remarks>
    /// 一般 and 介面 are unlikely to be the last two answers — realtime already has more than two —
    /// and a bool, or an ordinal, would have to change data format the day a third arrives. An
    /// ordinal would have been worse than useless through the v2 swap in particular: the two modes
    /// exchanged positions, so every stored 0 and 1 would have quietly come back as the other mode.
    /// A round trip through the file is what proves the choice actually survives.
    /// </remarks>
    [Theory]
    [InlineData(CaptureLayoutMode.General, "General")]
    [InlineData(CaptureLayoutMode.Interface, "Interface")]
    public void TheLayoutModeIsStoredByName(CaptureLayoutMode mode, string expectedName)
    {
        var written = new AppSettings();
        written.Capture.LayoutMode = mode;

        var json = SettingsService.Serialize(written);

        Assert.Contains($"\"LayoutMode\": \"{expectedName}\"", json);
        Assert.Equal(mode, SettingsService.Parse(json).Capture.LayoutMode);
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
        Assert.False(settings.QuickLookup.AutoCopyTranslation);
        Assert.False(settings.QuickLookup.ResultsCollapsed);
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
            RealtimePauseHotkeyModifiers = 5,
            RealtimePauseHotkeyVirtualKey = 0x44,
            RealtimePauseHotkeyDisplay = "Ctrl+Shift+D",
            RealtimePauseHotkeyEnabled = false,
            Capture =
            {
                VerticalText = true,
            },
            QuickLookup =
            {
                AutoCopyTranslation = true,
                ResultsCollapsed = true,
            },
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
        Assert.Equal(5u, settings.RealtimePauseHotkeyModifiers);
        Assert.Equal(0x44u, settings.RealtimePauseHotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimePauseHotkeyDisplay);
        Assert.False(settings.RealtimePauseHotkeyEnabled);
        Assert.True(settings.Capture.VerticalText);
        Assert.True(settings.QuickLookup.AutoCopyTranslation);
        Assert.True(settings.QuickLookup.ResultsCollapsed);
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
    public void AnUnreadableCaptureDirection_KeepsItsHorizontalDefault()
    {
        var settings = SettingsService.Parse(
            """{"Capture":{"VerticalText":"sometimes"},"Theme":"Light"}""");

        Assert.False(settings.Capture.VerticalText);
        Assert.Equal("Light", settings.Theme);
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
            + "  \"RealtimePauseHotkeyDisplay\": \"Ctrl+Shift+D\",\n"
            + "  \"ApiKey\": \"kept\"\n"
            + "}");

        // Left where they were, so they still have to survive the move happening around them.
        Assert.Equal("kept", settings.ApiKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimePauseHotkeyDisplay);

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
        var captureGroup = json.IndexOf("\"Capture\":", StringComparison.Ordinal);
        var quickLookupGroup = json.IndexOf("\"QuickLookup\":", StringComparison.Ordinal);
        var realtimeGroup = json.IndexOf("\"Realtime\":", StringComparison.Ordinal);
        var ocrDebugGroup = json.IndexOf("\"OcrDebug\":", StringComparison.Ordinal);
        var openAiGroup = json.IndexOf("\"OpenAi\":", StringComparison.Ordinal);
        var rootKeys = System.Text.Json.JsonDocument.Parse(json).RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            lastFlatKey >= 0 && captureGroup >= 0 && quickLookupGroup >= 0 && realtimeGroup >= 0
            && ocrDebugGroup >= 0 && openAiGroup >= 0);
        Assert.True(
            captureGroup > lastFlatKey,
            "grouped settings must be written after every flat one");
        Assert.Equal(
            ["Capture", "QuickLookup", "Realtime", "OcrDebug", "OpenAi"], rootKeys.TakeLast(5));
    }

    /// <summary>
    /// The file keeps what it holds as itself, not as a run of <c>\uXXXX</c>.
    /// </summary>
    /// <remarks>
    /// System.Text.Json escapes every non-ASCII character by default, and <c>+</c> with them. That
    /// was tolerable while the file held paths and key codes; the prompt library gave it names and
    /// prose the user wrote, and a settings file someone opens to check their own prompt is not
    /// allowed to be unreadable.
    ///
    /// Pinned because the encoder is one property that can fall off an options object without
    /// anything else noticing — it changes only how the file reads, so no other test would fail.
    /// Both halves are asserted: widening the character range alone fixes the Chinese and leaves
    /// every shortcut written as <c>Ctrl\u002BAlt\u002BA</c>.
    /// </remarks>
    [Fact]
    public void TheFileIsWrittenToBeRead()
    {
        var settings = new AppSettings();
        settings.OpenAi.AutoPrompts.Add(new OpenAiPromptPreset
        {
            Id = "a",
            Name = "測試用",
            Template = "翻成 {target_name}",
        });

        var json = SettingsService.Serialize(settings);

        Assert.Contains("\"Name\": \"測試用\"", json);
        Assert.Contains("翻成 {target_name}", json);
        Assert.Contains("\"HotkeyDisplay\": \"Ctrl+Alt+A\"", json);
        Assert.DoesNotContain(@"\u", json);
    }

    // Written before either shortcut could be switched off: both have to come back on, or upgrading
    // would silently disable two shortcuts the user still has.
    [Fact]
    public void MissingHotkeyEnabledSettings_StartOn()
    {
        var settings = SettingsService.Parse("""{"Theme":"Light"}""");

        Assert.True(settings.TranslationWindowHotkeyEnabled);
        Assert.True(settings.RealtimePauseHotkeyEnabled);
    }

    // Ctrl+Alt+S belonged to block framing until that shortcut was removed. A file written while it
    // did says nothing about 暫停 / 繼續, so that one arrives on its new default rather than on the
    // Ctrl+Alt+Q it used to have — and a user who had recorded their own keeps theirs.
    [Fact]
    public void TheKeyLeftBehindByBlockFramingBecomesThePauseDefault()
    {
        Assert.Equal("Ctrl+Alt+S", SettingsService.Parse("""{"Theme":"Light"}""")
            .RealtimePauseHotkeyDisplay);

        Assert.Equal("Ctrl+Alt+Q", SettingsService
            .Parse("""{"RealtimePauseHotkeyDisplay":"Ctrl+Alt+Q","RealtimePauseHotkeyVirtualKey":81}""")
            .RealtimePauseHotkeyDisplay);
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
