using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider { Google, Google2, Bing, Microsoft, DeepL, OpenAI }

public class AppSettings
{
    /// <summary>
    /// Identifies this installation across the diagnostic reports it sends.
    /// </summary>
    /// <remarks>
    /// First in the file because it is what a maintainer opening a bundle looks for first, and
    /// deliberately named for what it is rather than for the one thing it is used for today — it
    /// identifies an install, and diagnostics is only the first thing that wants to know.
    ///
    /// Empty is a valid state and the one every existing install starts from. See
    /// <see cref="Services.AppIdentityService"/> for what fills it and when.
    /// </remarks>
    public string ID { get; set; } = "";

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
    /// Pauses and resumes a running realtime session. Ctrl+Alt+S by default.
    /// </summary>
    /// <remarks>
    /// Stored as three fields — the modifiers and key Windows is given, plus the text the settings
    /// page shows — because the display string cannot be derived from the other two without a
    /// key-name table, and the recorder already has the user's own spelling of it at the moment they
    /// press the combination.
    ///
    /// Ctrl+Alt+S was the block-framing shortcut's default until that shortcut was removed: a session
    /// now begins by naming what it reads, which is a live window handle chosen from what is open,
    /// and no settings file can answer that. The key it left behind goes to the one realtime shortcut
    /// there still is. Anyone who had already recorded their own combination keeps it — a default
    /// only fills in what nobody has answered.
    /// </remarks>
    public uint RealtimePauseHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public uint RealtimePauseHotkeyVirtualKey { get; set; } = 0x53;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public string RealtimePauseHotkeyDisplay { get; set; } = "Ctrl+Alt+S";
    public ShortcutInputKind RealtimePauseHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimePauseHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimePauseHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Summons 取詞翻譯's popup over whatever the user is reading. Ctrl+Alt+Q by default.
    /// </summary>
    /// <remarks>
    /// Flat, with every other shortcut: 設定 owns them as one set on one page, and a shortcut filed
    /// under the feature it starts would be the only one of the four the user could not find beside
    /// its siblings.
    ///
    /// Q because the three combinations already taken are the letters of what they do (A, W, S) and
    /// this one is the 取 of 取詞 — and because Ctrl+Alt+Q is claimed by nothing on a stock Windows.
    /// </remarks>
    public uint QuickLookupHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public uint QuickLookupHotkeyVirtualKey { get; set; } = 0x51;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public string QuickLookupHotkeyDisplay { get; set; } = "Ctrl+Alt+Q";
    public ShortcutInputKind QuickLookupHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton QuickLookupHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickLookupHotkeyEnabled { get; set; } = true;

    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
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
    /// <see cref="TargetLanguage"/> or <see cref="RealtimeSettings.TargetLanguage"/>: what someone reads the
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
    /// The newest version the user has told us to stop interrupting them about, or empty for none.
    /// </summary>
    /// <remarks>
    /// Compared as a version rather than for equality, so it silences that release and nothing later:
    /// skipping 1.9.0 leaves 1.9.1 free to prompt again. It suppresses only the startup dialog — the
    /// nav rail still offers the update — because what the user declined was being interrupted, not
    /// the update itself. See <see cref="Services.UpdateNotifier"/>.
    /// </remarks>
    public string SkippedUpdateVersion { get; set; } = "";

    /// <summary>
    /// What 即時翻譯 keeps between sittings, grouped.
    /// </summary>
    /// <remarks>
    /// The first grouped section of this file, and the shape anything added from here on should
    /// follow — see <see cref="RealtimeSettings"/> for why the flat keys above it stayed flat.
    ///
    /// Declared last, and every later group belongs after it rather than beside the flat keys it
    /// relates to. Properties are written in declaration order, so this splits appsettings.json into
    /// two halves a reader can tell apart at a glance: everything that shipped before grouping
    /// existed, then everything that is grouped. Interleaving them would give the file no readable
    /// order at all — neither alphabetical, nor by feature, nor by age — and every group added later
    /// would have to find a home in the middle of the flat keys.
    /// </remarks>
    public RealtimeSettings Realtime { get; set; } = new();
}
