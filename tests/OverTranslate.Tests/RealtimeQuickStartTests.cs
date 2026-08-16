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
    public void AFirstRunIsTurnedAwayBecauseNoSourceLanguageHasBeenChosen()
    {
        // The realtime picker deliberately does not offer 自動, so a fresh install has nothing here
        // and the shortcut has nothing to translate from. Pressing it must say so rather than open a
        // framing layer that could never produce a translation.
        var quickStart = RealtimeQuickStart.From(new AppSettings());

        Assert.False(quickStart.CanStart);
        Assert.Equal("S.Realtime.ChooseSourceFirst", quickStart.BlockedReasonKey);
    }

    [Fact]
    public void AHandEditedAutomaticSourceIsTurnedAwayToo()
    {
        // Not reachable through the picker, which never offers it — reachable by editing the file.
        var settings = Ready();
        settings.RealtimeSourceLanguage = LanguageData.AutomaticSourceLanguage;

        var quickStart = RealtimeQuickStart.From(settings);

        Assert.False(quickStart.CanStart);
        Assert.Equal("S.Realtime.ChooseSourceFirst", quickStart.BlockedReasonKey);
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
