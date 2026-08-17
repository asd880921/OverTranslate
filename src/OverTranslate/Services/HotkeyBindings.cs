using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>The global shortcuts, in priority order — see <see cref="HotkeyBindings"/>.</summary>
public enum HotkeyAction
{
    Capture,
    TranslationWindow,
    Realtime,
    SingleShot,
}

/// <summary>
/// One physical shortcut trigger. Keyboard uses modifiers + virtual key; mouse middle has no code;
/// gamepad stores exactly one XInput button value in <see cref="Code"/>.
/// </summary>
public readonly record struct ShortcutTrigger(ShortcutInputKind Kind, uint Modifiers, uint Code)
{
    public static ShortcutTrigger Keyboard(uint modifiers, uint virtualKey) =>
        new(ShortcutInputKind.Keyboard, modifiers, virtualKey);

    public static ShortcutTrigger MouseMiddle() =>
        new(ShortcutInputKind.MouseMiddle, 0, 0);

    public static ShortcutTrigger Gamepad(GamepadShortcutButton button) =>
        new(ShortcutInputKind.Gamepad, 0, (uint)button);

    public uint VirtualKey => Kind == ShortcutInputKind.Keyboard ? Code : 0;
    public GamepadShortcutButton GamepadButton => Kind == ShortcutInputKind.Gamepad
        ? (GamepadShortcutButton)Code
        : GamepadShortcutButton.None;
}

/// <param name="ShadowedBy">
/// The higher-priority action holding this trigger, or null when this one gets it. Carried so the
/// settings page can say which shortcut took it rather than only that this one is off.
/// </param>
public readonly record struct HotkeyBinding(
    HotkeyAction Action,
    ShortcutTrigger Trigger,
    bool Enabled,
    HotkeyAction? ShadowedBy)
{
    /// <summary>Whether this one should actually be registered/observed.</summary>
    public bool IsActive => Enabled && ShadowedBy is null;

    // Compatibility/readability helpers for the keyboard registration path and existing tests.
    public ShortcutInputKind InputKind => Trigger.Kind;
    public uint Modifiers => Trigger.Modifiers;
    public uint VirtualKey => Trigger.VirtualKey;
    public GamepadShortcutButton GamepadButton => Trigger.GamepadButton;
}

/// <summary>
/// Works out which shortcuts are live when two of them want the same physical trigger.
/// </summary>
/// <remarks>
/// Keyboard combinations are registered with RegisterHotKey; mouse middle and gamepad buttons are
/// observed by the application's global input listener. They all share the same conflict resolver so
/// "mouse middle" cannot silently do two different things, and the same is true of one gamepad
/// button. Priority remains capture, translation window, realtime, then single-shot.
/// </remarks>
public static class HotkeyBindings
{
    /// <summary>Returns the trigger stored for one action.</summary>
    public static ShortcutTrigger TriggerFor(AppSettings settings, HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => BuildTrigger(
            settings.HotkeyInputKind,
            settings.HotkeyModifiers,
            settings.HotkeyVirtualKey,
            settings.HotkeyGamepadButton),
        HotkeyAction.TranslationWindow => BuildTrigger(
            settings.TranslationWindowHotkeyInputKind,
            settings.TranslationWindowHotkeyModifiers,
            settings.TranslationWindowHotkeyVirtualKey,
            settings.TranslationWindowHotkeyGamepadButton),
        HotkeyAction.Realtime => BuildTrigger(
            settings.RealtimeHotkeyInputKind,
            settings.RealtimeHotkeyModifiers,
            settings.RealtimeHotkeyVirtualKey,
            settings.RealtimeHotkeyGamepadButton),
        HotkeyAction.SingleShot => BuildTrigger(
            settings.SingleShotHotkeyInputKind,
            settings.SingleShotHotkeyModifiers,
            settings.SingleShotHotkeyVirtualKey,
            settings.SingleShotHotkeyGamepadButton),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static ShortcutTrigger BuildTrigger(
        ShortcutInputKind kind,
        uint modifiers,
        uint virtualKey,
        GamepadShortcutButton gamepadButton) => kind switch
    {
        ShortcutInputKind.MouseMiddle => ShortcutTrigger.MouseMiddle(),
        ShortcutInputKind.Gamepad when gamepadButton != GamepadShortcutButton.None =>
            ShortcutTrigger.Gamepad(gamepadButton),
        // A malformed/old setting that says Gamepad but has no button is safer as the keyboard it
        // already has stored than as a trigger that can never fire.
        _ => ShortcutTrigger.Keyboard(modifiers, virtualKey),
    };

    /// <summary>
    /// Every shortcut, in priority order, each marked with whether it is live and what took it.
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Resolve(AppSettings settings)
    {
        // Declaration order IS the priority order; nothing else encodes it.
        var declared = new (HotkeyAction Action, ShortcutTrigger Trigger, bool Enabled)[]
        {
            (HotkeyAction.Capture, TriggerFor(settings, HotkeyAction.Capture), true),
            (HotkeyAction.TranslationWindow,
                TriggerFor(settings, HotkeyAction.TranslationWindow),
                settings.TranslationWindowHotkeyEnabled),
            (HotkeyAction.Realtime,
                TriggerFor(settings, HotkeyAction.Realtime),
                settings.RealtimeHotkeyEnabled),
            (HotkeyAction.SingleShot,
                TriggerFor(settings, HotkeyAction.SingleShot),
                settings.SingleShotHotkeyEnabled),
        };

        var claimed = new Dictionary<ShortcutTrigger, HotkeyAction>();
        var resolved = new List<HotkeyBinding>(declared.Length);

        foreach (var (action, trigger, enabled) in declared)
        {
            HotkeyAction? shadowedBy = null;
            if (enabled)
            {
                if (claimed.TryGetValue(trigger, out var holder)) shadowedBy = holder;
                else claimed[trigger] = action;
            }

            resolved.Add(new HotkeyBinding(action, trigger, enabled, shadowedBy));
        }

        return resolved;
    }

    /// <summary>The shortcuts to register/observe, in priority order.</summary>
    public static IEnumerable<HotkeyBinding> Active(AppSettings settings) =>
        Resolve(settings).Where(binding => binding.IsActive);
}
