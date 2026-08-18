using OverTranslate.Models;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Whether a shortcut press can drop straight into block framing, and what to say when it cannot.
/// </summary>
/// <remarks>
/// Nothing calls this at the moment. The shortcut it exists for is turned away in
/// <c>MainWindow.OnRealtimeHotkeyPressed</c>, because a session now begins with a question this type
/// cannot answer from a settings file: which window, chosen from what is open right now. It is kept
/// whole rather than deleted while that is still an open product decision — see the shortcut's own
/// remarks — and everything below still describes what it does when it is called again.
///
/// The page's 開始 button asks the same questions, but it asks them of its own pickers. A shortcut has
/// no page — it can be pressed with the shell closed to the tray — so the answers have to come from
/// the settings file, and the ones that are not stored have to have somewhere to come from.
///
/// Only one thing can still refuse, and it is not a setting: with no monitor attached there is
/// nowhere to frame anything. Everything else resolves to a default rather than stopping the user,
/// including a source language left blank by a version that had no default for it — see
/// <see cref="LanguageData.GetValidRealtimeSourceCode"/>.
/// </remarks>
internal readonly record struct RealtimeQuickStart(
    RealtimeStartRequest? Request,
    string? BlockedReasonKey)
{
    /// <summary>Fewest and most blocks a session may frame — the page's stepper obeys the same pair.</summary>
    public const int MinBlocks = 1;

    /// <inheritdoc cref="MinBlocks"/>
    public const int MaxBlocks = 3;

    public bool CanStart => Request is not null;

    /// <summary>
    /// Reads the settings and either builds a request or names the first thing that is missing.
    /// </summary>
    /// <remarks>
    /// Whether a capture or another session is already running is not asked here: those are facts
    /// about the moment rather than about the settings, and the caller holds them.
    /// </remarks>
    public static RealtimeQuickStart From(AppSettings settings)
    {
        // Primary first, then whatever exists. The page prefers the monitor its own window sits on,
        // which is a better answer and one a shortcut cannot have — there may be no window open.
        var screens = Screen.AllScreens;
        var screen = Array.Find(screens, candidate => candidate.Primary) ?? screens.FirstOrDefault();
        if (screen is null)
            return new RealtimeQuickStart(null, "S.Realtime.NoScreens");

        return new RealtimeQuickStart(
            new RealtimeStartRequest(
                screen.Bounds,
                Math.Clamp(settings.Realtime.BlockCount, MinBlocks, MaxBlocks),
                LanguageData.GetValidRealtimeSourceCode(settings.Realtime.SourceLanguage),
                LanguageData.GetValidTargetCode(settings.Realtime.TargetLanguage),
                settings.Realtime.Provider,
                settings.Realtime.TextColor,
                settings.Realtime.ScrimColor,
                settings.Realtime.ScrimOpacity,
                settings.Realtime.NaturalBackgroundEnabled,
                settings.Realtime.SampleSourceTextColor),
            null);
    }
}
