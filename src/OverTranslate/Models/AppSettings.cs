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
    /// The shortcut that opens the translation window. Ctrl+Alt+W by default.
    /// </summary>
    /// <remarks>
    /// A convenience, not a headline: unlike the capture shortcut above it is not announced at
    /// startup and nothing in the interface advertises it, because the window it opens is already
    /// one click away in the tray. It opens and only opens — pressing it again brings the window
    /// forward rather than closing it, which is what every other way into this window does.
    /// </remarks>
    public uint TranslationWindowHotkeyModifiers { get; set; } = 3;

    public uint TranslationWindowHotkeyVirtualKey { get; set; } = 0x57;

    public string TranslationWindowHotkeyDisplay { get; set; } = "Ctrl+Alt+W";
    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
    /// <summary>
    /// The language 即時翻譯 reads from, or empty for "not chosen yet".
    /// </summary>
    /// <remarks>
    /// Empty rather than <see cref="LanguageData.DefaultOcrSourceLanguage"/>, which is 自動 — a mode
    /// the realtime picker deliberately does not offer, because recognition there gets one look at a
    /// frame and no retry. A blank field asks the question; a default would answer it badly. Only a
    /// language the user picked themselves is ever written here, so what comes back is always an
    /// answer they gave once.
    /// </remarks>
    public string RealtimeSourceLanguage { get; set; } = "";
    public string RealtimeTargetLanguage { get; set; } = LanguageData.DefaultTargetLanguage;
    public TranslationProvider RealtimeProvider { get; set; } = TranslationProvider.Microsoft;
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// The OpenAI-compatible server to talk to, or empty for
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.DefaultBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Empty rather than a copy of the default, for the same reason the prompt and the model are —
    /// see <see cref="OpenAiPromptAuto"/>.
    /// </remarks>
    public string OpenAiBaseUrl { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "";

    /// <summary>
    /// The instruction sent to an OpenAI-compatible model, or empty to use the built-in one.
    /// </summary>
    /// <remarks>
    /// Editable because the right wording belongs to the model, not to this app: a translation-only
    /// model is trained on "translate from X to Y" and degrades when handed anything else, while a
    /// general chat model and a reasoning model each want something different again — the
    /// &lt;think&gt; stripping elsewhere in the provider is the same problem showing through. One
    /// built-in wording cannot serve all three, and whoever picked a local model can write a line
    /// of prose.
    ///
    /// Two of them because these are two different sentences rather than one sentence with a blank:
    /// with a source language chosen the model is told to translate from it, and with 自動 there is
    /// no language to name, so that wording has no <c>{source}</c> to fill at all.
    ///
    /// Empty rather than a copy of the default text, so anyone who never edits keeps following the
    /// built-in wording as it improves — stamping today's into the settings file would freeze them
    /// on it forever. See <see cref="Services.Providers.OpenAiCompatibleProvider.BuildPrompt"/>.
    /// </remarks>
    public string OpenAiPromptAuto { get; set; } = "";

    /// <inheritdoc cref="OpenAiPromptAuto"/>
    public string OpenAiPromptExplicit { get; set; } = "";

    /// <summary>
    /// Whether the request carries a temperature at all.
    /// </summary>
    /// <remarks>
    /// Separate from the value because "no temperature" is not a number: the reasoning models on the
    /// hosted APIs reject the field outright rather than clamping it, so a request to them has to
    /// leave it out. On by default, which is what every local server expects.
    /// </remarks>
    public bool OpenAiTemperatureEnabled { get; set; } = true;

    /// <summary>
    /// How much randomness the model is asked for, when <see cref="OpenAiTemperatureEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Zero because this is translation: the same line on screen should come back the same way twice.
    /// Editable because the value that means "as literal as possible" is the model's to define — some
    /// small local ones loop on repeated output at 0 and need a little slack to come out of it.
    /// </remarks>
    public double OpenAiTemperature { get; set; }
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
    /// needs it every time.
    /// </summary>
    public string RealtimeTextColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultText;
    /// <inheritdoc cref="RealtimeTextColor"/>
    public string RealtimeScrimColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultScrim;

    /// <summary>
    /// How opaque the band behind the subtitle is drawn, 0–100 — see
    /// <see cref="Services.Realtime.RealtimeSubtitleColors.MinScrimOpacity"/>.
    /// </summary>
    /// <remarks>
    /// Its own key rather than an alpha channel on <see cref="RealtimeScrimColor"/>, which could have
    /// carried one as <c>#AARRGGBB</c>. Two reasons, and the second is the one that decided it: the
    /// colour is picked in a system dialog that has no alpha to give back, so the two values do not
    /// arrive together; and a reader who wants the band lighter over one game and heavier over the
    /// next is changing this and not their colour, which a combined key would make them redo.
    /// </remarks>
    public int RealtimeScrimOpacity { get; set; } =
        Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity;

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
