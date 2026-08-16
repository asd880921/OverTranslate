using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>The three global shortcuts, in priority order — see <see cref="HotkeyBindings"/>.</summary>
public enum HotkeyAction
{
    Capture,
    TranslationWindow,
    Realtime,
}

/// <param name="ShadowedBy">
/// The higher-priority action holding this combination, or null when this one gets it. Carried so the
/// settings page can say which shortcut took it rather than only that this one is off.
/// </param>
public readonly record struct HotkeyBinding(
    HotkeyAction Action,
    uint Modifiers,
    uint VirtualKey,
    bool Enabled,
    HotkeyAction? ShadowedBy)
{
    /// <summary>Whether this one should actually be registered with Windows.</summary>
    public bool IsActive => Enabled && ShadowedBy is null;
}

/// <summary>
/// Works out which shortcuts are live when two of them want the same combination.
/// </summary>
/// <remarks>
/// Windows keys a registration by window and combination, so the second claim on one is simply
/// refused: <c>RegisterHotKey</c> returns false and nothing else happens. Which of the two loses is
/// then whichever happened to be registered second, i.e. an ordering nobody chose.
///
/// The settings page already refuses to RECORD a combination another shortcut holds, so a user
/// cannot create the clash by hand. Stored settings can still arrive in one anyway, and that is what
/// this exists for: adding <see cref="HotkeyAction.Realtime"/> gave every existing installation a
/// Ctrl+Alt+S it never agreed to, and anyone who had already put Ctrl+Alt+S on the translation
/// window would have had one of the two stop working with no explanation.
///
/// So the order is declared rather than discovered, and it runs from the feature the application is
/// for down to the most recently added: capture, then the translation window, then realtime. A
/// shortcut shadowed by a higher one is reported rather than silently dropped.
/// </remarks>
public static class HotkeyBindings
{
    /// <summary>
    /// Every shortcut, in priority order, each marked with whether it is live and what took it.
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Resolve(AppSettings settings)
    {
        // Declaration order IS the priority order; nothing else encodes it.
        var declared = new (HotkeyAction Action, uint Modifiers, uint Key, bool Enabled)[]
        {
            (HotkeyAction.Capture, settings.HotkeyModifiers, settings.HotkeyVirtualKey, true),
            (HotkeyAction.TranslationWindow,
                settings.TranslationWindowHotkeyModifiers,
                settings.TranslationWindowHotkeyVirtualKey,
                settings.TranslationWindowHotkeyEnabled),
            (HotkeyAction.Realtime,
                settings.RealtimeHotkeyModifiers,
                settings.RealtimeHotkeyVirtualKey,
                settings.RealtimeHotkeyEnabled),
        };

        var claimed = new Dictionary<(uint, uint), HotkeyAction>();
        var resolved = new List<HotkeyBinding>(declared.Length);

        foreach (var (action, modifiers, key, enabled) in declared)
        {
            // A disabled shortcut does not claim its combination, so turning one off hands the
            // combination to whatever was shadowed by it rather than leaving it reserved.
            HotkeyAction? shadowedBy = null;
            if (enabled)
            {
                if (claimed.TryGetValue((modifiers, key), out var holder)) shadowedBy = holder;
                else claimed[(modifiers, key)] = action;
            }

            resolved.Add(new HotkeyBinding(action, modifiers, key, enabled, shadowedBy));
        }

        return resolved;
    }

    /// <summary>The shortcuts to register, in priority order.</summary>
    public static IEnumerable<HotkeyBinding> Active(AppSettings settings) =>
        Resolve(settings).Where(binding => binding.IsActive);
}
