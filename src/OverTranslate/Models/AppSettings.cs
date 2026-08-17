using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider { Google, Google2, Bing, Microsoft, DeepL, OpenAI }

public class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyVirtualKey { get; set; } = 0x41;
    public string HotkeyDisplay { get; set; } = "Ctrl+Alt+A";
    public ShortcutInputKind HotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton HotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

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
    public ShortcutInputKind TranslationWindowHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton TranslationWindowHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// Whether the translation-window shortcut is registered at all.
    /// </summary>
    /// <remarks>
    /// There is no matching field for the capture shortcut, and deliberately not: that one is the
    /// feature the application exists for, so its checkbox is ticked and disabled rather than backed
    /// by a value. A stored flag that must always be true is a way to end up with it false.
    /// </remarks>
    public bool TranslationWindowHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// The shortcut that adds an 即時翻譯 block. Ctrl+Alt+S by default.
    /// </summary>
    /// <remarks>
    /// Stored as the same three fields as the two shortcuts above — the modifiers and key Windows is
    /// given, plus the text the settings page shows — because the display string cannot be derived
    /// from the other two without a key-name table, and the recorder already has the user's own
    /// spelling of it at the moment they press the combination.
    /// </remarks>
    public uint RealtimeHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="RealtimeHotkeyModifiers"/>
    public uint RealtimeHotkeyVirtualKey { get; set; } = 0x53;

    /// <inheritdoc cref="RealtimeHotkeyModifiers"/>
    public string RealtimeHotkeyDisplay { get; set; } = "Ctrl+Alt+S";
    public ShortcutInputKind RealtimeHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimeHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimeHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Pauses and resumes a running realtime session. Ctrl+Alt+P by default.
    /// </summary>
    /// <remarks>
    /// Its own shortcut rather than a second meaning for the capture one. Sharing it made a kind of
    /// sense — a session hides the page the capture shortcut would otherwise open, so the key was
    /// free — but it also meant the reader could not choose what to press for the thing they reach for
    /// most, and could not put it somewhere their hands already are while a game has the screen.
    /// </remarks>
    public uint RealtimePauseHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public uint RealtimePauseHotkeyVirtualKey { get; set; } = 0x50;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public string RealtimePauseHotkeyDisplay { get; set; } = "Ctrl+Alt+P";
    public ShortcutInputKind RealtimePauseHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimePauseHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimePauseHotkeyEnabled { get; set; } = true;

    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
    /// <summary>
    /// The language 即時翻譯 reads from.
    /// </summary>
    /// <remarks>
    /// English rather than <see cref="LanguageData.DefaultOcrSourceLanguage"/>, which is 自動: the
    /// realtime picker does not offer that mode, because recognition there gets one look at a frame
    /// and no retry.
    ///
    /// This was deliberately blank once, on the reasoning that a guessed source language is worse
    /// than a question — a blank field asks, a default answers badly. It was changed because the
    /// question had to be asked from somewhere, and the shortcut that opens block framing has no
    /// page to ask from; the alternative was a notification refusing to start, which is friction on
    /// every first use to protect against a setting one trip to the page fixes.
    ///
    /// The cost is real and worth writing down: someone whose content is not English, who never
    /// opens the realtime page and starts from the shortcut, gets English recognition over it and
    /// nothing says why. See <see cref="LanguageData.GetValidRealtimeSourceCode"/>, which is where a
    /// blank from before this default is resolved.
    /// </remarks>
    public string RealtimeSourceLanguage { get; set; } = LanguageData.DefaultRealtimeSourceLanguage;
    public string RealtimeTargetLanguage { get; set; } = LanguageData.DefaultTargetLanguage;

    /// <summary>
    /// How many blocks a session is set up to frame, 1–3.
    /// </summary>
    /// <remarks>
    /// Kept, unlike everything else about a sitting, because the shortcut has to answer the same
    /// question the page's stepper does and cannot read it off a page that is not open. Without this
    /// the shortcut would always offer one block while the button offered three, which is the same
    /// control giving two different answers.
    /// </remarks>
    public int RealtimeBlockCount { get; set; } = 1;
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
    /// Whether the band behind a subtitle is replaced by a repair of the picture underneath: the
    /// source line is erased and filled in from the pixels around it, and the translation is drawn
    /// back where it was.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately not because it is new. The repair interpolates from the edges
    /// of the line it erases, so it is close to invisible over a subtitle's own dark strip or a
    /// dialogue panel and visibly a smeared patch over texture, a face or a grid — a higher ceiling
    /// than the band and a lower floor. Which of the two is better is a judgement about the picture
    /// the user is watching, and only they can see it, so the honest default is the one whose result
    /// is predictable. It also costs a screen grab several times a second, which the band does not:
    /// see <see cref="RealtimeSampleSourceTextColor"/> for the other half of the same trade.
    /// </remarks>
    public bool RealtimeNaturalBackgroundEnabled { get; set; } = false;

    /// <summary>
    /// Whether the translation is drawn in a colour sampled from the source line rather than in
    /// <see cref="RealtimeTextColor"/>.
    /// </summary>
    /// <remarks>
    /// Its own key rather than travelling with <see cref="RealtimeNaturalBackgroundEnabled"/>, because
    /// the two fail in different places and a reader may well want one without the other: "repair the
    /// background, but keep my high-contrast yellow" is a reasonable arrangement, and sampling is
    /// exactly what stops being reliable on the busy pictures where that yellow earns its keep.
    ///
    /// Sampling averages the pixels that stand out from the local background, so it has no outline,
    /// shadow or gradient to give back. Source text that depends on one of those to stay readable
    /// comes back as a colour that does not, which is what the readability check falls back to this
    /// setting for.
    /// </remarks>
    public bool RealtimeSampleSourceTextColor { get; set; } = false;

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
