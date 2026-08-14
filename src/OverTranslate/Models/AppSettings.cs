using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider { Google, Google2, Bing, Microsoft, DeepL, OpenAI }

public class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyVirtualKey { get; set; } = 0x41;
    public string HotkeyDisplay { get; set; } = "Ctrl+Alt+A";

    /// <summary>
    /// The shortcut that opens the translation window. Ctrl+Alt+S by default.
    /// </summary>
    /// <remarks>
    /// A convenience, not a headline: unlike the capture shortcut above it is not announced at
    /// startup and nothing in the interface advertises it, because the window it opens is already
    /// one click away in the tray. It opens and only opens — pressing it again brings the window
    /// forward rather than closing it, which is what every other way into this window does.
    /// </remarks>
    public uint TranslationWindowHotkeyModifiers { get; set; } = 3;

    public uint TranslationWindowHotkeyVirtualKey { get; set; } = 0x53;

    public string TranslationWindowHotkeyDisplay { get; set; } = "Ctrl+Alt+S";
    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
    public string RealtimeTargetLanguage { get; set; } = LanguageData.DefaultTargetLanguage;
    public TranslationProvider RealtimeProvider { get; set; } = TranslationProvider.Microsoft;
    public string ApiKey { get; set; } = "";
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    /// <summary>
    /// The interface language, "zh-Hant" or "en". Empty means "not chosen yet".
    /// </summary>
    /// <remarks>
    /// Empty rather than a hardcoded default so a first run can follow the OS language — see
    /// <see cref="Services.LocalizationService.ResolveSystemDefault"/>. Once the user picks one it
    /// is stored verbatim and the OS is never consulted again, because an explicit choice should
    /// survive someone changing their Windows display language.
    ///
    /// This is the interface language only. It has no bearing on
    /// <see cref="TargetLanguage"/> or <see cref="RealtimeTargetLanguage"/>: what someone reads the
    /// buttons in and what they want subtitles translated into are unrelated, and a Taiwanese user
    /// running the app in English still wants Chinese output.
    /// </remarks>
    public string UiLanguage { get; set; } = "";
    public bool AutoTranslateAfterSelection { get; set; } = false;
    public bool SaveScreenshotToDisk { get; set; } = false;
    /// <summary>Empty means "use ScreenshotSaveService.DefaultDirectory" (圖片\OverTranslate).</summary>
    public string ScreenshotSavePath { get; set; } = "";
    /// <summary>Off by default: Debug records the recognised text, i.e. the user's screen contents.</summary>
    public bool VerboseLogging { get; set; } = false;
    /// <summary>
    /// Realtime subtitle colours, "#RRGGBB". Unlike everything else on 即時翻譯 these are kept: which
    /// screen and how many blocks belong to one sitting, but a reader who needs yellow on dark blue
    /// needs it every time. The scrim's alpha is not stored — see RealtimeSubtitleColors.
    /// </summary>
    public string RealtimeTextColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultText;
    /// <inheritdoc cref="RealtimeTextColor"/>
    public string RealtimeScrimColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultScrim;

    /// <summary>
    /// Whether the per-block framing guidance on the edit layer is unfolded. Expanded on a first run,
    /// because the guidance is what stops a badly framed block.
    /// </summary>
    /// <remarks>
    /// One value for the whole feature rather than one per block: a user who has read the guidance has
    /// read it, and folding it away on every block of every sitting to say so is the same instruction
    /// being dismissed over and over. Whichever block's chevron is pressed last writes here, and the
    /// next edit layer opens every block that way. Deliberately not shown on the settings page — it is
    /// a state the control sets by being used, not a preference anyone would go looking for.
    /// </remarks>
    public bool RealtimeGuidanceExpanded { get; set; } = true;

    /// <summary>
    /// The newest version the user has told us to stop interrupting them about, or empty for none.
    /// </summary>
    /// <remarks>
    /// Compared as a version rather than for equality, so it silences that release and nothing later:
    /// skipping 1.9.0 leaves 1.9.1 free to prompt again. It suppresses only the startup dialog — the
    /// nav rail still offers the update — because what the user declined was being interrupted, not
    /// the update itself. See <see cref="Services.UpdateNotifier"/>.
    /// </remarks>
    public string SkippedUpdateVersion { get; set; } = "";
}
