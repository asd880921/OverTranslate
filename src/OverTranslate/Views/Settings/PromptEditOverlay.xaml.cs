using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Writes one named prompt — the card that opens over
/// <see cref="ServiceSettingsOverlay"/> when a prompt is added or edited.
/// </summary>
/// <remarks>
/// The one surface in 設定 that does not save as it is typed. Everything behind it is a value that
/// takes effect the moment it changes; a prompt is prose, and half a sentence saved on the way to
/// the whole one is what the model would be sent on the next capture. So this card is confirmed,
/// and cancelling it leaves the stored prompt exactly as it was.
///
/// It writes the settings itself rather than handing a result back, because what it changes is a
/// list rather than a field: adding, renaming and deleting are three different edits to it, and
/// the panel behind only ever needs to be told that the list moved.
/// </remarks>
public partial class PromptEditOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>How many lines of prompt the box accepts.</summary>
    private const int PromptMaxLines = 200;

    /// <summary>True while the box is being cut back to the line limit, so its own edit is ignored.</summary>
    private bool _trimming;

    /// <summary>True while the card is being filled in, so initialization never trims or writes.</summary>
    private bool _loading;

    /// <summary>Which of the two lists this prompt belongs to.</summary>
    private bool _automatic;

    /// <summary>
    /// The preset being edited, or empty while a new one is being written.
    /// </summary>
    /// <remarks>
    /// The id rather than the object, so a save cannot write onto a preset that was removed from
    /// the list while this card was open — it looks the preset up again at the moment it commits.
    /// </remarks>
    private string _editingId = "";

    /// <summary>Raised once the stored prompt library has changed, before the card closes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once the card has gone, saved or not, so the panel behind can take focus back.</summary>
    public event EventHandler? Closed;

    public PromptEditOverlay()
    {
        InitializeComponent();

        // On the control rather than checked on save: a name too long to fit the row it is drawn in
        // should stop being typed, not be silently shortened once it is too late to see.
        NameBox.MaxLength = OpenAiSettings.MaxNameLength;
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    /// <param name="preset">The prompt to edit, or null to write a new one.</param>
    /// <param name="suggestedName">
    /// What to call a new prompt until the user renames it. Ignored when editing.
    /// </param>
    public void Open(bool automatic, OpenAiPromptPreset? preset, string suggestedName)
    {
        _automatic = automatic;
        _editingId = preset?.Id ?? "";

        _loading = true;
        try
        {
            TitleText.Text = LocalizationService.Get(
                preset is null ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");
            ScopeHint.Text = LocalizationService.Get(
                automatic ? "S.Settings.PromptAutoScope" : "S.Settings.PromptExplicitScope");

            NameBox.Text = preset?.Name ?? suggestedName;

            // A new prompt opens on the built-in wording rather than on an empty box: it is the one
            // worked example of these parameters there is, and most edits are a sentence away from
            // it rather than a page of prose from nothing.
            TemplateBox.Text = preset?.Template ?? OpenAiCompatibleProvider.DefaultPromptTemplate(automatic);

            // 自動 has no source language, so the two rows describing one would be listing
            // parameters that resolve to nothing. Hidden whole rather than left showing an empty
            // example.
            var sourceRows = automatic ? Visibility.Collapsed : Visibility.Visible;
            ParamRowSourceName.Visibility = sourceRows;
            ParamRowSourceCode.Visibility = sourceRows;

            // Nothing to delete while the prompt does not exist yet.
            DeleteButton.Visibility = preset is null ? Visibility.Collapsed : Visibility.Visible;

            // Left open by a card that was closed from inside the confirmation, otherwise.
            ConfirmLayer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }

        UpdateChrome();

        Visibility = Visibility.Visible;

        LocalizationService.LanguageChanged += OnLanguageChanged;

        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // The name is what a new prompt is missing, and selected rather than merely focused so the
        // suggested one is replaced by typing instead of typed around.
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Close()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();
            Closed?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // See ServiceSettingsOverlay.ReleaseAnimations: HoldEnd would keep these properties under the
    // animation clock, and the composition layer with them, long after the move is over.
    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;

        Card.CacheMode = null;
    }

    /// <summary>
    /// Only the labels this card composes in code — the title and the scope note. The boxes hold
    /// what the user typed, in whatever language they typed it.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        TitleText.Text = LocalizationService.Get(
            _editingId.Length == 0 ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");
        ScopeHint.Text = LocalizationService.Get(
            _automatic ? "S.Settings.PromptAutoScope" : "S.Settings.PromptExplicitScope");

        // Only while the question is on screen; it names the prompt, so it is composed rather than
        // bound and has to be written again in the new language.
        if (ConfirmLayer.Visibility == Visibility.Visible) WriteConfirmMessage();
    }

    /// <summary>
    /// Swallows the click without dismissing. Handled so it does not reach the panel behind, whose
    /// own scrim does close on a click.
    /// </summary>
    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled, or the panel behind this one would take the same Escape and close as well.
        if (e.Key == Key.Escape)
        {
            // The question first: Escape answers the thing on top, and dismissing the card out from
            // under an unanswered confirmation would look like the delete had happened.
            if (ConfirmLayer.Visibility == Visibility.Visible) CloseConfirm();
            else Close();

            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The trim below raises this event again for its own edit.
        if (_trimming) return;

        if (!_loading) TrimToLineLimit();

        UpdateChrome();
    }

    /// <summary>Puts the built-in wording back in the box, to write over or to write from.</summary>
    private void LoadDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Through the selection rather than by assigning Text, which would throw away the undo
        // history: this replaces something the user wrote, and Ctrl+Z getting it back is what makes
        // a confirmation unnecessary.
        TemplateBox.SelectAll();
        TemplateBox.SelectedText = OpenAiCompatibleProvider.DefaultPromptTemplate(_automatic);
        TemplateBox.Focus();
    }

    /// <summary>
    /// Brings the save button in line with what is on screen: a prompt needs a name to be picked
    /// out of the list by and a sentence to send, and neither can be supplied later.
    /// </summary>
    private void UpdateChrome()
    {
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        SaveButton.IsEnabled =
            NameBox.Text.Trim().Length > 0 && TemplateBox.Text.Trim().Length > 0;
    }

    /// <summary>
    /// Drops anything past <see cref="PromptMaxLines"/> lines, silently.
    /// </summary>
    /// <remarks>
    /// A cap on the input rather than a check further in: the prompt is sent once per recognised
    /// block, so a pasted document is a real cost repeated a dozen times over, and the place to
    /// stop it is where it arrives. Nothing is said about it — the box visibly refuses to grow,
    /// which is the whole message, and a warning about a limit nobody reaches by writing an
    /// instruction would only be in the way.
    ///
    /// Removed through the selection so the paste stays undoable; the trim is then simply applied
    /// again if the undone text is still too long.
    /// </remarks>
    private void TrimToLineLimit()
    {
        var overflow = LineLimitOverflowIndex(TemplateBox.Text, PromptMaxLines);
        if (overflow < 0) return;

        _trimming = true;
        try
        {
            TemplateBox.Select(overflow, TemplateBox.Text.Length - overflow);
            TemplateBox.SelectedText = "";
            TemplateBox.CaretIndex = overflow;
        }
        finally
        {
            _trimming = false;
        }
    }

    /// <summary>
    /// Where the text passes <paramref name="maxLines"/> lines, or -1 when it does not.
    /// </summary>
    /// <remarks>
    /// Hard line breaks only. <see cref="TextBox.LineCount"/> counts the lines actually drawn, so
    /// with wrapping on it would make the cap depend on how wide the window happens to be.
    /// </remarks>
    internal static int LineLimitOverflowIndex(string text, int maxLines)
    {
        var index = -1;
        for (var line = 0; line < maxLines; line++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0) return -1;
        }

        // Cut before the break that would have started the next line, and before the carriage
        // return in front of it, so the kept text does not end on a half of a CRLF pair.
        return index > 0 && text[index - 1] == '\r' ? index - 1 : index;
    }

    // ── Committing ───────────────────────────────────────────────────────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var template = TemplateBox.Text.Trim();
        if (name.Length == 0 || template.Length == 0) return;

        var openAi = SettingsService.Instance.Current.OpenAi;
        var presets = openAi.PresetsFor(_automatic);

        // Looked up again rather than held: the list is the stored one, and this card has been open
        // for as long as someone took to write a paragraph.
        var existing = presets.FirstOrDefault(p => p.Id == _editingId);
        if (existing is not null)
        {
            existing.Name = name;
            existing.Template = template;
        }
        else
        {
            // The panel does not offer to add past the cap, so this only fires if the list filled
            // up behind this card. Closing without writing is the honest answer.
            if (presets.Count >= OpenAiSettings.MaxPresets)
            {
                Close();
                return;
            }

            var created = new OpenAiPromptPreset
            {
                Id = OpenAiSettings.NewId(),
                Name = name,
                Template = template,
            };
            presets.Add(created);

            // A prompt someone just wrote is a prompt they want used. Adding one and then having to
            // pick it in the list would make the first half of that gesture do nothing.
            openAi.SelectPreset(_automatic, created.Id);
        }

        Commit();
    }

    /// <summary>
    /// Asks before deleting — the one confirmation in the application.
    /// </summary>
    /// <remarks>
    /// Every other destructive gesture here is undoable (Ctrl+Z in a box) or costs a setting that is
    /// one click to restore. This one throws away prose someone wrote, with nothing to get it back,
    /// from a button that shares a row with 取消 and 儲存.
    /// </remarks>
    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        WriteConfirmMessage();
        ConfirmLayer.Visibility = Visibility.Visible;

        // The safe half takes focus, so Enter or Space arriving from the click that opened this
        // lands on 取消 rather than on the button that deletes.
        ConfirmCancelButton.Focus();
    }

    /// <summary>Names the prompt in the question, so the reader is not asked about "the prompt".</summary>
    private void WriteConfirmMessage()
    {
        // The box rather than the stored preset: a rename typed but not yet saved is still what the
        // user is looking at, and asking about the old name would be asking about something else.
        ConfirmMessage.Text = LocalizationService.Format(
            "S.Settings.PromptDeleteConfirm", NameBox.Text.Trim());
    }

    private void CloseConfirm() => ConfirmLayer.Visibility = Visibility.Collapsed;

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e) => CloseConfirm();

    /// <summary>Swallows the click. Dismissing on it would answer the question by accident.</summary>
    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirm();

        var openAi = SettingsService.Instance.Current.OpenAi;
        openAi.PresetsFor(_automatic).RemoveAll(p => p.Id == _editingId);

        // Back to the built-in wording rather than to whichever prompt happens to be next in the
        // list: the provider resolves an id that names nothing the same way, and a silent move onto
        // a neighbouring prompt would change what gets sent without saying so.
        if (openAi.SelectedIdFor(_automatic) == _editingId)
            openAi.SelectPreset(_automatic, "");

        Commit();
    }

    private void Commit()
    {
        SettingsService.Instance.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
