using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider { Google, Google2, Bing, Microsoft, Yandex, DeepL }

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
    public string ApiKey { get; set; } = "";
    public string Theme { get; set; } = "Dark";
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
}
