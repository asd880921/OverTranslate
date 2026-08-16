using OverTranslate.Models;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Whether a shortcut press can drop straight into block framing, and what to say when it cannot.
/// </summary>
/// <remarks>
/// The page's 開始 button asks the same questions, but it asks them of its own pickers. A shortcut has
/// no page — it can be pressed with the shell closed to the tray — so the answers have to come from
/// the settings file, and the ones that are not stored have to have somewhere to come from.
///
/// The source language is the one that actually stops people: 即時翻譯 deliberately does not offer 自動
/// (recognition there gets one look at a frame and no retry), so a first run has nothing in it and a
/// shortcut pressed then would otherwise open a framing layer that cannot translate anything. That is
/// what <see cref="BlockedReasonKey"/> exists to say, out loud, through a notification.
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
    /// Checked in the same order as the button, so the two paths refuse for the same reason when more
    /// than one thing is wrong. Whether a capture or another session is already running is not asked
    /// here: those are facts about the moment rather than about the settings, and the caller holds
    /// them.
    /// </remarks>
    public static RealtimeQuickStart From(AppSettings settings)
    {
        var source = LanguageData.GetValidOcrSourceCode(settings.RealtimeSourceLanguage);
        if (string.IsNullOrWhiteSpace(source) || LanguageData.IsAutomaticSource(source))
            return new RealtimeQuickStart(null, "S.Realtime.ChooseSourceFirst");

        // Primary first, then whatever exists. The page prefers the monitor its own window sits on,
        // which is a better answer and one a shortcut cannot have — there may be no window open.
        var screens = Screen.AllScreens;
        var screen = Array.Find(screens, candidate => candidate.Primary) ?? screens.FirstOrDefault();
        if (screen is null)
            return new RealtimeQuickStart(null, "S.Realtime.NoScreens");

        return new RealtimeQuickStart(
            new RealtimeStartRequest(
                screen.Bounds,
                Math.Clamp(settings.RealtimeBlockCount, MinBlocks, MaxBlocks),
                source,
                LanguageData.GetValidTargetCode(settings.RealtimeTargetLanguage),
                settings.RealtimeProvider,
                settings.RealtimeTextColor,
                settings.RealtimeScrimColor,
                settings.RealtimeScrimOpacity),
            null);
    }
}
