namespace OverTranslate.Models;

/// <summary>
/// One named instruction the user keeps for an OpenAI-compatible model.
/// </summary>
/// <remarks>
/// Stored rather than edited in place because a prompt is worth keeping more than one of: the
/// wording a translation-only model wants and the wording a reasoning model wants are different
/// sentences, and someone pointing this provider at both in turn should not have to retype one to
/// try the other. Naming them is what makes the list readable — "翻譯用" beside "測試用" says which
/// is which, and the first forty characters of two prompts that both open "You are a" does not.
/// </remarks>
public class OpenAiPromptPreset
{
    /// <summary>
    /// Identifies this preset for <see cref="OpenAiSettings.SelectedAutoPromptId"/> and its
    /// explicit twin.
    /// </summary>
    /// <remarks>
    /// A stored id rather than the position in the list, because deleting the first of three
    /// presets would otherwise silently move the selection onto a different prompt. Assigned once
    /// when the preset is created — see <see cref="OpenAiSettings.NewId"/> — and never rewritten,
    /// so the selection survives a rename.
    /// </remarks>
    public string Id { get; set; } = "";

    /// <summary>What the list calls it. The user's own words, capped at a readable length.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The instruction itself, with the language placeholders left unfilled — see
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.BuildPrompt"/>.
    /// </summary>
    public string Template { get; set; } = "";
}

/// <summary>
/// Everything the OpenAI-compatible provider keeps that is not one value on one line.
/// </summary>
/// <remarks>
/// A group of its own, following <see cref="RealtimeSettings"/>: the prompts are a list of objects
/// rather than a value, and a list belongs under the feature that owns it rather than flat beside
/// the endpoint and the model. Those — <see cref="AppSettings.OpenAiBaseUrl"/>,
/// <see cref="AppSettings.OpenAiApiKey"/>, <see cref="AppSettings.OpenAiModel"/> and the
/// temperature pair — deliberately stay where they shipped: moving a key renames it, and the user
/// who updates would come back to an endpoint they have to type again. Nothing here shipped
/// before, so nothing here has that cost.
///
/// The two prompt keys that did ship — <c>OpenAiPromptAuto</c> and <c>OpenAiPromptExplicit</c> —
/// were dropped rather than migrated, on the owner's call: a single stored prompt has no name, and
/// carrying one into a named list would mean inventing a name for it.
/// </remarks>
public class OpenAiSettings
{
    /// <summary>
    /// How many presets the user may keep per case.
    /// </summary>
    /// <remarks>
    /// Five, on top of the built-in one that is always there. A cap rather than an open list
    /// because this is a settings panel and not a library: the list is picked from in place,
    /// without a scroller of its own, and five named prompts is already more than the two wordings
    /// this provider is realistically pointed at. Nothing breaks at six — the cap is there to keep
    /// the panel legible, so the Add button simply stops offering.
    /// </remarks>
    public const int MaxPresets = 5;

    /// <summary>How long a preset name may be. Enough for a phrase, not for a sentence.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The user's own prompts for 自動 source.</summary>
    public List<OpenAiPromptPreset> AutoPrompts { get; set; } = [];

    /// <inheritdoc cref="AutoPrompts"/>
    public List<OpenAiPromptPreset> ExplicitPrompts { get; set; } = [];

    /// <summary>
    /// Which prompt 自動 source sends, by <see cref="OpenAiPromptPreset.Id"/>, or empty for the
    /// built-in one.
    /// </summary>
    /// <remarks>
    /// Empty is both the default and the fallback: an id naming a preset that has since been
    /// deleted resolves to the built-in wording rather than to nothing, so a settings file edited
    /// by hand cannot leave the provider with no instruction to send.
    /// </remarks>
    public string SelectedAutoPromptId { get; set; } = "";

    /// <inheritdoc cref="SelectedAutoPromptId"/>
    public string SelectedExplicitPromptId { get; set; } = "";

    /// <summary>The presets for one case — 自動 source, or a chosen source language.</summary>
    public List<OpenAiPromptPreset> PresetsFor(bool automatic) =>
        automatic ? AutoPrompts : ExplicitPrompts;

    /// <inheritdoc cref="SelectedAutoPromptId"/>
    public string SelectedIdFor(bool automatic) =>
        automatic ? SelectedAutoPromptId : SelectedExplicitPromptId;

    public void SelectPreset(bool automatic, string id)
    {
        if (automatic) SelectedAutoPromptId = id;
        else SelectedExplicitPromptId = id;
    }

    /// <summary>The selected preset for one case, or null when the built-in wording is in use.</summary>
    public OpenAiPromptPreset? SelectedPreset(bool automatic)
    {
        var id = SelectedIdFor(automatic);
        if (id.Length == 0) return null;

        return PresetsFor(automatic).FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// The instruction the selected preset holds, or empty to mean "use the built-in one".
    /// </summary>
    /// <remarks>
    /// Empty rather than a copy of the built-in text, which is the contract the single stored
    /// prompt kept before this list existed: anyone who never picks a preset of their own keeps
    /// following the built-in wording as it improves, instead of being frozen on whatever it said
    /// the day they installed.
    /// </remarks>
    public string TemplateFor(bool automatic) => SelectedPreset(automatic)?.Template ?? "";

    /// <summary>A fresh <see cref="OpenAiPromptPreset.Id"/>, unique against everything stored.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
