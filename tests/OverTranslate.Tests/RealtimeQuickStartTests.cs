using OverTranslate.Models;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeQuickStartTests
{
    private static AppSettings Ready() => new()
    {
        RealtimeSourceLanguage = "JA",
        RealtimeTargetLanguage = "ZH-HANT",
        RealtimeBlockCount = 2,
    };

    [Fact]
    public void AFirstRunStartsOnTheDefaultSourceLanguage()
    {
        // This used to refuse and send the user to the page to choose one. It answers instead, so
        // that pressing the shortcut on a fresh install opens block framing rather than a
        // notification — the trade is recorded on AppSettings.RealtimeSourceLanguage.
        var quickStart = RealtimeQuickStart.From(new AppSettings());

        Assert.True(quickStart.CanStart);
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, quickStart.Request!.SourceLanguage);
    }

    [Theory]
    [InlineData("")]                                       // written by a version with no default
    [InlineData(LanguageData.AutomaticSourceLanguage)]     // hand-edited; the picker never offers it
    [InlineData("XX")]                                     // a code retired since
    public void AnUnusableStoredSourceFallsBackRatherThanReachingTheSession(string stored)
    {
        // 自動 is the one that matters: realtime gets one look at a frame, so a mode that guesses per
        // frame would make a subtitle track flicker between languages.
        var settings = Ready();
        settings.RealtimeSourceLanguage = stored;

        var quickStart = RealtimeQuickStart.From(settings);

        Assert.True(quickStart.CanStart);
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, quickStart.Request!.SourceLanguage);
    }

    [Fact]
    public void AConfiguredInstallationStartsWithNothingToSay()
    {
        var quickStart = RealtimeQuickStart.From(Ready());

        Assert.True(quickStart.CanStart);
        Assert.Null(quickStart.BlockedReasonKey);
        Assert.Equal("JA", quickStart.Request!.SourceLanguage);
        Assert.Equal(2, quickStart.Request.MaxBlocks);
    }

    [Fact]
    public void TheAdvancedEffectsAreOffOnAnInstallationThatNeverAskedForThem()
    {
        // The point of the pair being settings at all: a session built from a stored default must
        // draw the band the reader chose the colours of, not a repair of the picture underneath.
        var request = RealtimeQuickStart.From(Ready()).Request!;

        Assert.False(request.NaturalBackground);
        Assert.False(request.SampleSourceTextColor);
    }

    [Fact]
    public void EachAdvancedEffectReachesTheSessionOnItsOwn()
    {
        // Independently, because they fail in different places: repairing the background while
        // keeping a high-contrast colour is a reasonable arrangement, and so is the reverse.
        var background = Ready();
        background.RealtimeNaturalBackgroundEnabled = true;

        var colour = Ready();
        colour.RealtimeSampleSourceTextColor = true;

        var withBackground = RealtimeQuickStart.From(background).Request!;
        var withColour = RealtimeQuickStart.From(colour).Request!;

        Assert.True(withBackground.NaturalBackground);
        Assert.False(withBackground.SampleSourceTextColor);
        Assert.False(withColour.NaturalBackground);
        Assert.True(withColour.SampleSourceTextColor);
    }

    [Theory]
    [InlineData(0, RealtimeQuickStart.MinBlocks)]
    [InlineData(-4, RealtimeQuickStart.MinBlocks)]
    [InlineData(99, RealtimeQuickStart.MaxBlocks)]
    public void ABlockCountFromOutsideTheStepperIsBroughtBackIntoRange(int stored, int expected)
    {
        // The stepper cannot produce these; a text file can, and the framing layer would be asked
        // for a number of blocks it has no way to draw.
        var settings = Ready();
        settings.RealtimeBlockCount = stored;

        Assert.Equal(expected, RealtimeQuickStart.From(settings).Request!.MaxBlocks);
    }
}
