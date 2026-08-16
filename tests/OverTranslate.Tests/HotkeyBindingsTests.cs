using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class HotkeyBindingsTests
{
    private const uint CtrlAlt = 3;

    [Fact]
    public void ThreeDistinctCombinationsAllStayOn()
    {
        var active = HotkeyBindings.Active(new AppSettings()).ToList();

        Assert.Equal(
            [HotkeyAction.Capture, HotkeyAction.TranslationWindow, HotkeyAction.Realtime],
            active.Select(binding => binding.Action));
    }

    [Fact]
    public void TheShortcutAddedLastLosesToOneSomebodyAlreadyChose()
    {
        // The upgrade this whole type exists for. Realtime defaults to Ctrl+Alt+S, and an existing
        // installation may already have put Ctrl+Alt+S on the translation window — a combination its
        // owner picked, against one they have never seen. Left to Windows, whichever registered
        // second would simply fail and the user would be told nothing.
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x53,
            TranslationWindowHotkeyDisplay = "Ctrl+Alt+S",
        };

        var resolved = HotkeyBindings.Resolve(settings);
        var window = resolved.Single(b => b.Action == HotkeyAction.TranslationWindow);
        var realtime = resolved.Single(b => b.Action == HotkeyAction.Realtime);

        Assert.True(window.IsActive);
        Assert.False(realtime.IsActive);
        Assert.Equal(HotkeyAction.TranslationWindow, realtime.ShadowedBy);
    }

    [Fact]
    public void CaptureOutranksEverythingBecauseItIsWhatTheApplicationIsFor()
    {
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x41, // the capture default
            RealtimeHotkeyModifiers = CtrlAlt,
            RealtimeHotkeyVirtualKey = 0x41,
        };

        var resolved = HotkeyBindings.Resolve(settings);

        Assert.True(resolved.Single(b => b.Action == HotkeyAction.Capture).IsActive);
        Assert.Equal(
            HotkeyAction.Capture,
            resolved.Single(b => b.Action == HotkeyAction.TranslationWindow).ShadowedBy);
        Assert.Equal(
            HotkeyAction.Capture,
            resolved.Single(b => b.Action == HotkeyAction.Realtime).ShadowedBy);
    }

    [Fact]
    public void SwitchingOffTheHolderHandsTheCombinationDown()
    {
        // A shortcut that is off does not reserve its combination. Without this, turning the window
        // shortcut off would leave the realtime one still shadowed by something no longer running,
        // which is the kind of state a user cannot reason their way out of.
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x53,
            TranslationWindowHotkeyEnabled = false,
        };

        var realtime = HotkeyBindings.Resolve(settings)
            .Single(binding => binding.Action == HotkeyAction.Realtime);

        Assert.True(realtime.IsActive);
        Assert.Null(realtime.ShadowedBy);
    }

    [Fact]
    public void ASwitchedOffShortcutIsNotRegisteredEvenWithNothingInItsWay()
    {
        var settings = new AppSettings { RealtimeHotkeyEnabled = false };

        var realtime = HotkeyBindings.Resolve(settings)
            .Single(binding => binding.Action == HotkeyAction.Realtime);

        Assert.False(realtime.IsActive);
        Assert.Null(realtime.ShadowedBy); // off by choice, not because something took it
        Assert.DoesNotContain(
            HotkeyBindings.Active(settings), binding => binding.Action == HotkeyAction.Realtime);
    }

    [Fact]
    public void CaptureCannotBeSwitchedOff()
    {
        // There is no setting for it — see AppSettings.TranslationWindowHotkeyEnabled for why — so
        // this pins that the resolver never reports it as anything but enabled.
        Assert.True(HotkeyBindings.Resolve(new AppSettings())
            .Single(binding => binding.Action == HotkeyAction.Capture).Enabled);
    }
}
