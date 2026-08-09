namespace OverTranslate.Services.Realtime;

/// <summary>
/// The wording that tells the user the capture shortcut re-translates a running realtime session.
/// </summary>
/// <remarks>
/// The floating control bar tooltip and 運作方式 both name the key the user actually has bound
/// rather than the default. Shared here so the shortcut is read and normalized in one place.
///
/// The shortcut can be cleared in 設定. In that state the settings-page line disappears, while the
/// icon tooltip still names the clickable action without inventing a keyboard equivalent.
/// </remarks>
public static class RealtimeRefreshHint
{
    /// <summary>The shortcut currently bound to 截圖翻譯, as 設定 displays it.</summary>
    public static string CurrentHotkey => SettingsService.Instance.Current.HotkeyDisplay;

    /// <summary>
    /// For the refresh icon in the translating control bar. The button already communicates the
    /// action visually; the tooltip supplies its exact meaning and the equivalent keyboard path.
    /// </summary>
    public static string ForControlTooltip(string? hotkey) =>
        Normalize(hotkey) is { Length: > 0 } key
            ? $"刷新當前畫面翻譯內容 ({key})"
            : "刷新當前畫面翻譯內容";

    /// <summary>
    /// For 運作方式, where there is room to say why anyone would want this — the reason is not
    /// obvious until the user has been stuck behind a frame that stopped changing.
    /// </summary>
    public static string ForSettingsPage(string? hotkey) =>
        Normalize(hotkey) is { Length: > 0 } key
            ? $"4. 翻譯中按 {key}，可重新辨識並翻譯目前畫面。"
            : "";

    private static string Normalize(string? hotkey) => hotkey?.Trim() ?? "";
}
