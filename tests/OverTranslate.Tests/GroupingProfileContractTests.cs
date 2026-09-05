using System.Reflection;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The grouping thresholds come from the capture mode the user chose, and from nothing else.
/// </summary>
/// <remarks>
/// This is the guard against the mode quietly becoming a second source language. The layout-metric
/// split was spent removing one input that steered grouping without saying so; a mode is allowed to
/// steer it, but only through this one table, and only as far as the two fields on the profile.
/// </remarks>
public class GroupingProfileContractTests
{
    [Fact]
    public void EachCaptureMode_MapsToItsOwnProfile()
    {
        Assert.Same(GroupingProfile.Interface, GroupingProfile.For(CaptureLayoutMode.Interface));
        Assert.Same(GroupingProfile.General, GroupingProfile.For(CaptureLayoutMode.General));

        // Not one profile under two names. The two hold the same figures until the steps that
        // relax them land, so this is about identity rather than value: what must hold throughout
        // is that each mode owns its own instance, so that moving one never moves the other.
        Assert.NotSame(
            GroupingProfile.For(CaptureLayoutMode.Interface),
            GroupingProfile.For(CaptureLayoutMode.General));
    }

    /// <summary>
    /// The live-screen profile is reachable from no capture mode.
    /// </summary>
    /// <remarks>
    /// Realtime has no toolbar and no mode to honour. Sharing the screenshot side's profile would
    /// hold for exactly as long as neither number moved, and then tie two paths together that were
    /// deliberately separated — silently, in whichever step next touched a threshold.
    /// </remarks>
    [Fact]
    public void TheRealtimeProfile_IsNotWhatAnyCaptureModeSelects()
    {
        foreach (var mode in Enum.GetValues<CaptureLayoutMode>())
            Assert.NotSame(GroupingProfile.Realtime, GroupingProfile.For(mode));
    }

    /// <summary>
    /// Every capture mode has an entry, including one added later.
    /// </summary>
    [Fact]
    public void EveryCaptureMode_HasAProfile()
    {
        foreach (var mode in Enum.GetValues<CaptureLayoutMode>())
            Assert.NotNull(GroupingProfile.For(mode));
    }

    /// <summary>
    /// The profile carries two thresholds, and a third one arriving has to be a decision rather
    /// than a habit.
    /// </summary>
    /// <remarks>
    /// Not a type-system lock — a sealed constructor would only produce a test-only way around it.
    /// This is here so that adding a field fails a test whose name says what the reviewer has to be
    /// shown: a measured case from the image corpus, and a design change, per profile field.
    /// </remarks>
    [Fact]
    public void TheProfile_CarriesTwoThresholds_AndAThirdIsADesignChange()
    {
        var fields = typeof(GroupingProfile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(
            [nameof(GroupingProfile.TightlySetMinTextSizeRatio),
             nameof(GroupingProfile.WaiveLengthTestWhenSetSolid)],
            fields);
    }

    /// <summary>
    /// The live-screen profile holds its own figure, written out rather than referred to.
    /// </summary>
    /// <remarks>
    /// The three profiles quote one constant today, so lowering that constant — which the design
    /// allows as a shared improvement to the screenshot side — would move the live-screen path with
    /// it, silently, while every identity assertion above stayed green. The literal here is not a
    /// claim that this number must never change: it is there so that changing it needs someone to
    /// come and answer whether realtime changes too.
    /// </remarks>
    [Fact]
    public void TheRealtimeProfile_HoldsItsOwnNumber_EvenWhenItMatchesTheScreenshotSide()
    {
        Assert.Equal(0.88, GroupingProfile.Realtime.TightlySetMinTextSizeRatio);
        Assert.False(GroupingProfile.Realtime.WaiveLengthTestWhenSetSolid);
    }

    /// <summary>
    /// The standard profile is today's behaviour, stated as a number rather than as a promise.
    /// </summary>
    [Fact]
    public void TheStandardProfile_HoldsTheThresholdsInUseBeforeModesExisted()
    {
        Assert.Equal(
            OcrTextBlockGrouper.MinTextSizeRatio,
            GroupingProfile.Interface.TightlySetMinTextSizeRatio);
        Assert.False(GroupingProfile.Interface.WaiveLengthTestWhenSetSolid);
    }
}
