using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Views.Controls;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Modal panel holding everything one translation service has to be told — a key for DeepL, an
/// endpoint and a prompt for OpenAI. Drawn on top of the shell rather than in a window of its own,
/// like <see cref="Shell.AboutOverlay"/>, so the app keeps a single visible surface.
/// </summary>
/// <remarks>
/// These settings persist the moment a control changes, the same contract the settings page keeps,
/// so every handler routes through <see cref="Persist"/> and is inert while <see cref="_loading"/>
/// is set. There is no OK button to press and nothing is discarded on close.
/// </remarks>
public partial class ServiceSettingsOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    // Typing shouldn't hit the disk on every keystroke; the value is written once typing pauses.
    private static readonly TimeSpan EditDebounce = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _apiKeyDebounce;
    private readonly DispatcherTimer _openAiSettingsDebounce;
    private readonly DispatcherTimer _promptDebounce;

    private const int PromptAutoSegment = 0;
    private const int PromptExplicitSegment = 1;

    /// <summary>How many lines of prompt the box accepts.</summary>
    private const int PromptMaxLines = 200;

    /// <summary>True while the box is being cut back to the line limit, so its own edit is ignored.</summary>
    private bool _trimmingPrompt;

    /// <summary>
    /// Which of the two prompts the editor is currently holding. Kept alongside the tab's own
    /// checked state because a pending edit has to be written to the prompt it was typed into, even
    /// if the user has since switched to the other tab.
    /// </summary>
    private int _promptSegment = PromptAutoSegment;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    /// <summary>Which service is on screen. Decides which panel is shown and what the title says.</summary>
    private TranslationProvider _provider = TranslationProvider.DeepL;

    /// <summary>Raised once the panel has been dismissed, so the page behind it can re-read what changed.</summary>
    public event EventHandler? Closed;

    public ServiceSettingsOverlay()
    {
        InitializeComponent();

        _apiKeyDebounce = new DispatcherTimer { Interval = EditDebounce };
        _apiKeyDebounce.Tick += (_, _) =>
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Secret.Trim());
        };

        _openAiSettingsDebounce = new DispatcherTimer { Interval = EditDebounce };
        _openAiSettingsDebounce.Tick += (_, _) =>
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Secret.Trim();
                s.OpenAiModel = OpenAiModelBox.Text.Trim();
                s.OpenAiTemperature = ReadTemperature();
            });
        };

        _promptDebounce = new DispatcherTimer { Interval = EditDebounce };
        _promptDebounce.Tick += (_, _) =>
        {
            _promptDebounce.Stop();
            PersistPrompt();
        };
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open(TranslationProvider provider)
    {
        _provider = provider;
        LoadSettings();

        Visibility = Visibility.Visible;

        // The panel is only listening while it is on screen, and the settings page behind it
        // re-reads the same strings on its own.
        LocalizationService.LanguageChanged += OnLanguageChanged;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated,
        // then ramps snapping back on over roughly a second once the motion stops — see
        // AboutOverlay.Open for why the card is cached as a bitmap for the length of the scale.
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

        // Focus lets the control receive Escape without the page underneath stealing it
        Focus();
    }

    public void Close()
    {
        // Nothing may be left sitting in a timer: the page behind this one re-reads the stored
        // settings the moment it is told the panel closed, and a pending edit would be invisible
        // to it until the timer happened to fire.
        FlushPendingEdits();

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

    // DoubleAnimation defaults to FillBehavior.HoldEnd, so the animated properties stay under the
    // animation clock's control long after the animation has visually finished. Handing them back
    // to their owners drops the intermediate composition layer as soon as the transition is over.
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

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the card must not bubble up to the scrim's dismiss handler
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            TitleText.Text = LocalizationService.Format(
                "S.Settings.ServiceDialogTitle", LanguageData.GetProviderDisplay(_provider));

            var deepL = _provider == TranslationProvider.DeepL;
            DeepLPanel.Visibility = deepL ? Visibility.Visible : Visibility.Collapsed;
            OpenAiPanel.Visibility = deepL ? Visibility.Collapsed : Visibility.Visible;

            // The card is sized for the wider of the two panels, and DeepL is one field.
            Card.Width = deepL ? 460 : 620;

            ApiKeyBox.Secret = s.ApiKey;

            OpenAiBaseUrlBox.Text = s.OpenAiBaseUrl;
            OpenAiApiKeyBox.Secret = s.OpenAiApiKey;
            OpenAiModelBox.Text = s.OpenAiModel;
            TemperatureEnabledCheckBox.IsChecked = s.OpenAiTemperatureEnabled;
            TemperatureBox.Text = FormatTemperature(s.OpenAiTemperature);
            LoadPromptEditor(s);

            UpdateOpenAiFieldChrome();
            UpdateTemperatureChrome();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Re-renders the text this panel composes in code: the title, the prompt switch's segment
    /// labels, and the built-in wording shown behind an empty prompt box.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => LoadSettings();

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
    }

    /// <summary>Writes out whatever is still waiting on a debounce timer.</summary>
    private void FlushPendingEdits()
    {
        if (_apiKeyDebounce.IsEnabled)
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Secret.Trim());
        }

        if (_openAiSettingsDebounce.IsEnabled)
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Secret.Trim();
                s.OpenAiModel = OpenAiModelBox.Text.Trim();
                s.OpenAiTemperature = ReadTemperature();
            });
        }

        FlushPromptEdit();
    }

    // ── DeepL ────────────────────────────────────────────────────────────────

    private void ApiKeyBox_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _apiKeyDebounce.Stop();
        _apiKeyDebounce.Start();
    }

    // ── OpenAI fields ────────────────────────────────────────────────────────

    private void OpenAiSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Unconditionally: the placeholders answer what an empty box will do, and waiting for the
        // debounce would leave them a beat behind the typing.
        UpdateOpenAiFieldChrome();

        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    /// <summary>
    /// Shows what each empty box falls back to, in place of the empty box.
    /// </summary>
    private void UpdateOpenAiFieldChrome()
    {
        OpenAiBaseUrlPlaceholder.Text = OpenAiCompatibleProvider.DefaultBaseUrl;
        OpenAiBaseUrlPlaceholder.Visibility =
            OpenAiBaseUrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        OpenAiModelPlaceholder.Text = OpenAiCompatibleProvider.DefaultModel;
        OpenAiModelPlaceholder.Visibility =
            OpenAiModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAiSecret_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    // ── OpenAI advanced settings ─────────────────────────────────────────────

    /// <summary>How long the advanced section takes to open or close.</summary>
    private static readonly Duration AdvancedDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Widest temperature any of these APIs accepts; the field is clamped to it.</summary>
    private const double MaxTemperature = 2;

    private bool _openAiAdvancedExpanded;

    /// <summary>
    /// Which open/close is current, so a run that is replaced part way through does not then finish
    /// and hand the section a height belonging to the state it was leaving.
    /// </summary>
    private int _openAiAdvancedTransition;

    private void OpenAiAdvancedToggle_Click(object sender, RoutedEventArgs e) =>
        SetOpenAiAdvancedExpanded(!_openAiAdvancedExpanded);

    /// <remarks>
    /// The height is animated from the content's measured height rather than from a number written
    /// here, and handed back to Auto once open: the sentences inside are localized and wrap against
    /// the card's width, so today's measurement is not tomorrow's.
    /// </remarks>
    private void SetOpenAiAdvancedExpanded(bool expanded)
    {
        _openAiAdvancedExpanded = expanded;
        var transition = ++_openAiAdvancedTransition;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OpenAiAdvancedChevronRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(expanded ? 180 : 0, AdvancedDuration) { EasingFunction = ease });

        // Enabled for the whole of the opening move, and only switched off once the closing one has
        // finished — closed, its content has to be out of the tab order as well as out of sight,
        // which a zero height alone would not manage.
        if (expanded) OpenAiAdvancedHost.IsEnabled = true;

        var from = OpenAiAdvancedHost.ActualHeight;
        double to = 0;
        if (expanded)
        {
            var width = OpenAiAdvancedHost.ActualWidth;
            OpenAiAdvancedContent.Measure(new Size(
                width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
            to = OpenAiAdvancedContent.DesiredSize.Height;
        }

        var height = new DoubleAnimation(from, to, AdvancedDuration) { EasingFunction = ease };
        height.Completed += (_, _) =>
        {
            if (transition != _openAiAdvancedTransition) return;
            OpenAiAdvancedHost.BeginAnimation(HeightProperty, null);
            if (expanded)
            {
                OpenAiAdvancedHost.Height = double.NaN;
            }
            else
            {
                OpenAiAdvancedHost.Height = 0;
                OpenAiAdvancedHost.IsEnabled = false;
            }
        };

        OpenAiAdvancedHost.BeginAnimation(HeightProperty, height);
        OpenAiAdvancedHost.BeginAnimation(
            OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, AdvancedDuration));
    }

    private void TemperatureEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateTemperatureChrome();
        if (_loading) return;
        Persist(s => s.OpenAiTemperatureEnabled = TemperatureEnabledCheckBox.IsChecked == true);
    }

    private void TemperatureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTemperatureChrome();

        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    /// <summary>Back to sending a temperature, and back to zero.</summary>
    private void TemperatureResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Assigned before the checkbox so its own handler, which reads the box, sees the new value.
        TemperatureBox.Text = FormatTemperature(0);
        TemperatureEnabledCheckBox.IsChecked = true;

        _openAiSettingsDebounce.Stop();
        Persist(s =>
        {
            s.OpenAiTemperatureEnabled = true;
            s.OpenAiTemperature = 0;
        });

        UpdateTemperatureChrome();
    }

    /// <summary>
    /// Puts the field back in agreement with what will actually be sent: an empty box, a number out
    /// of range, or something that is not a number at all all become the value stored for them.
    /// </summary>
    /// <remarks>
    /// On leaving the field rather than on each keystroke, so half-typed input is left alone —
    /// "0." is on the way to "0.5" and rewriting it mid-word would take the decimal point back out.
    /// </remarks>
    private void TemperatureBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = ReadTemperature();
        var text = FormatTemperature(value);
        if (TemperatureBox.Text != text) TemperatureBox.Text = text;

        _openAiSettingsDebounce.Stop();
        Persist(s => s.OpenAiTemperature = value);
    }

    /// <summary>The value in the box, or 0 for anything that is not a number in range.</summary>
    private double ReadTemperature()
    {
        var text = TemperatureBox.Text.Trim();

        // The invariant form first because that is what the field is written back as and what the
        // API takes; the user's own is tried after it, so a comma typed on a locale that uses one
        // is read as the decimal point it was meant to be.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return 0;

        return Math.Clamp(value, 0, MaxTemperature);
    }

    private static string FormatTemperature(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Brings the field and its reset in line with what is on screen: nothing to type into while the
    /// parameter is not being sent, and nothing to restore while both halves are already the default.
    /// </summary>
    private void UpdateTemperatureChrome()
    {
        var enabled = TemperatureEnabledCheckBox.IsChecked == true;
        TemperatureBox.IsEnabled = enabled;
        TemperatureRangeHint.Opacity = enabled ? 1 : 0.45;

        // The box rather than the stored value, so this answers on the first keystroke instead of
        // when the debounce eventually fires.
        TemperatureResetButton.IsEnabled = !enabled || TemperatureBox.Text.Trim() != FormatTemperature(0);
    }

    // ── Prompt editor ────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the tabs and the editor. Also the language-change path, since the note under the tabs
    /// and the built-in wording shown behind an empty box are both localized.
    /// </summary>
    /// <remarks>
    /// Only ever reached with <see cref="_loading"/> set, which is what keeps checking the tab here
    /// from running <see cref="PromptTab_Checked"/> and reloading the editor a second time.
    /// </remarks>
    private void LoadPromptEditor(AppSettings s)
    {
        if (_promptSegment == PromptAutoSegment) PromptAutoTab.IsChecked = true;
        else PromptExplicitTab.IsChecked = true;

        PromptBox.Text = _promptSegment == PromptAutoSegment ? s.OpenAiPromptAuto : s.OpenAiPromptExplicit;
        UpdatePromptChrome();
    }

    private void PromptTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // The pending edit belongs to the prompt it was typed into, so it is written out before the
        // editor is handed to the other one.
        FlushPromptEdit();

        _promptSegment = PromptExplicitTab.IsChecked == true ? PromptExplicitSegment : PromptAutoSegment;

        var s = SettingsService.Instance.Current;
        var text = _promptSegment == PromptAutoSegment ? s.OpenAiPromptAuto : s.OpenAiPromptExplicit;

        // Assigning Text would raise TextChanged and start the debounce, which would then write
        // this prompt straight back over itself; _loading is what the rest of the panel uses to mean
        // "this change came from us, not the user".
        _loading = true;
        try { PromptBox.Text = text; }
        finally { _loading = false; }

        UpdatePromptChrome();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The trim below raises this event again for its own edit.
        if (_trimmingPrompt) return;

        // Only what the user types or pastes is held to the limit. A longer prompt already in the
        // settings file is left alone until they touch it, rather than being quietly cut down on a
        // panel they only came to look at.
        if (!_loading) TrimPromptToLineLimit();

        // Chrome unconditionally: the reset button describes what is in the box right now, and
        // waiting for the debounce would leave it a beat behind the typing.
        UpdatePromptChrome();

        if (_loading) return;
        _promptDebounce.Stop();
        _promptDebounce.Start();
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
    private void TrimPromptToLineLimit()
    {
        var overflow = LineLimitOverflowIndex(PromptBox.Text, PromptMaxLines);
        if (overflow < 0) return;

        _trimmingPrompt = true;
        try
        {
            PromptBox.Select(overflow, PromptBox.Text.Length - overflow);
            PromptBox.SelectedText = "";
            PromptBox.CaretIndex = overflow;
        }
        finally
        {
            _trimmingPrompt = false;
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

    private void PromptResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Cleared through the selection rather than by assigning Text, which would throw away the
        // undo history: this discards something the user wrote, and Ctrl+Z getting it back is what
        // makes a confirmation prompt unnecessary.
        PromptBox.SelectAll();
        PromptBox.SelectedText = "";
        PromptBox.Focus();

        _promptDebounce.Stop();
        PersistPrompt();
    }

    private void FlushPromptEdit()
    {
        if (!_promptDebounce.IsEnabled) return;
        _promptDebounce.Stop();
        PersistPrompt();
    }

    private void PersistPrompt()
    {
        var text = PromptBox.Text.Trim();
        var segment = _promptSegment;

        Persist(s =>
        {
            if (segment == PromptAutoSegment) s.OpenAiPromptAuto = text;
            else s.OpenAiPromptExplicit = text;
        });

        UpdatePromptChrome();
    }

    /// <summary>
    /// Brings the reset button and the placeholder in line with what is on screen.
    /// </summary>
    private void UpdatePromptChrome()
    {
        var automatic = _promptSegment == PromptAutoSegment;

        PromptTabHint.Text = LocalizationService.Get(
            automatic ? "S.Settings.PromptAutoHint" : "S.Settings.PromptExplicitHint");

        // Nothing to restore while the built-in one is in use, and a live button that does nothing
        // would be the only control here that lies about having something to do. This reads the
        // box rather than the stored setting, so it answers on the first keystroke instead of when
        // the debounce eventually fires.
        PromptResetButton.IsEnabled = PromptBox.Text.Trim().Length > 0;

        PromptPlaceholder.Visibility = PromptBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WritePlaceholderAware(
            PromptPlaceholder, OpenAiCompatibleProvider.DefaultPromptTemplate(automatic));

        // 自動 has no source language, so the two rows describing one would be listing parameters
        // that resolve to nothing. Hidden whole rather than left showing an empty example.
        var sourceRows = automatic ? Visibility.Collapsed : Visibility.Visible;
        ParamRowSourceName.Visibility = sourceRows;
        ParamRowSourceCode.Visibility = sourceRows;
    }

    /// <summary>
    /// Writes prose that mentions the prompt placeholders, with each picked out in the accent colour.
    /// </summary>
    /// <remarks>
    /// They are the only part of the sentence that is machinery rather than words — what the user
    /// types into their own prompt to have a language substituted in — and the colour is what says
    /// so without a sentence explaining it. The same colour the rest of the app uses for the thing
    /// being pointed at, through a resource reference so it follows a theme change.
    /// </remarks>
    private static void WritePlaceholderAware(TextBlock target, string text)
    {
        target.Inlines.Clear();
        foreach (var (segment, isPlaceholder) in SplitOnPlaceholders(text))
        {
            if (isPlaceholder)
            {
                var placeholder = new Run(segment);
                placeholder.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");
                target.Inlines.Add(placeholder);
                continue;
            }

            // The prose between placeholders may carry a line break — the explicit hint lists the
            // source pair and the target pair one per line. Added as a LineBreak rather than left in
            // a Run, so it does not depend on the block's wrapping to show up.
            var lines = segment.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0) target.Inlines.Add(new Run(lines[i]));
            }
        }
    }

    /// <summary>
    /// Splits text into runs of ordinary prose and the placeholder tokens between them, in order.
    /// </summary>
    private static IEnumerable<(string Text, bool IsPlaceholder)> SplitOnPlaceholders(string text)
    {
        // All four, or the tag placeholders would be the only machinery in the sentence left looking
        // like prose. None is a prefix of another once the closing brace is counted, so the
        // earliest-match loop below cannot pick the wrong one.
        string[] tokens =
        [
            OpenAiCompatibleProvider.SourcePlaceholder,
            OpenAiCompatibleProvider.TargetPlaceholder,
            OpenAiCompatibleProvider.SourceCodePlaceholder,
            OpenAiCompatibleProvider.TargetCodePlaceholder,
        ];

        var index = 0;
        while (index < text.Length)
        {
            var at = -1;
            var length = 0;
            foreach (var token in tokens)
            {
                var found = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0 || (at >= 0 && found >= at)) continue;
                at = found;
                length = token.Length;
            }

            if (at < 0)
            {
                yield return (text[index..], false);
                yield break;
            }

            if (at > index) yield return (text[index..at], false);
            yield return (text.Substring(at, length), true);
            index = at + length;
        }
    }

    private void OllamaGuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
