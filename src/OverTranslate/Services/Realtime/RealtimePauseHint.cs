namespace OverTranslate.Services.Realtime;

/// <summary>
/// The wording that tells the user the capture shortcut pauses and resumes a running realtime
/// session.
/// </summary>
/// <remarks>
/// The floating control bar tooltip and 運作方式 both name the key the user actually has bound
/// rather than the default. Shared here so the shortcut is read and normalized in one place.
///
/// The shortcut can be cleared in 設定. In that state the settings-page line disappears, while the
/// icon tooltip still names the clickable action without inventing a keyboard equivalent.
/// </remarks>
public static class RealtimePauseHint
{
    /// <summary>The shortcut currently bound to 截圖翻譯, as 設定 displays it.</summary>
    public static string CurrentHotkey => SettingsService.Instance.Current.HotkeyDisplay;

    /// <summary>
    /// For the pause/resume icon in the translating control bar. The button already communicates the
    /// action visually; the tooltip supplies its exact meaning and the equivalent keyboard path.
    /// </summary>
    /// <param name="paused">
    /// The session's current state, not the action — a paused session's button offers 繼續.
    /// </param>
    public static string ForControlTooltip(string? hotkey, bool paused)
    {
        var action = paused ? "繼續即時翻譯" : "暫停即時翻譯";

        return Normalize(hotkey) is { Length: > 0 } key ? $"{action} ({key})" : action;
    }

    /// <summary>
    /// For 運作方式, where there is room to say what pausing actually does — that it releases the
    /// recognition model rather than merely freezing the picture, which is the part that decides
    /// whether a user reaches for it when they want their machine back.
    /// </summary>
    public static string ForSettingsPage(string? hotkey) =>
        Normalize(hotkey) is { Length: > 0 } key
            ? $"4. 翻譯中按 {key} 可暫停／繼續，暫停時會清除譯文並釋放辨識模型。"
            : "";

    private static string Normalize(string? hotkey) => hotkey?.Trim() ?? "";
}
