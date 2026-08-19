using System.Windows;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

namespace OverTranslate.Views.Capture;

public partial class ToolbarWindow : Window
{
    private const string SpeakGlyph = Controls.TtsGlyphs.Speak;
    private const string StopGlyph = Controls.TtsGlyphs.Stop;

    /// <summary>
    /// The eye the toggle shows while the translation is on screen, and the struck-through one it
    /// shows while the original is.
    /// </summary>
    /// <remarks>
    /// Both are the action, like the label beside them: pressing the first reveals the original,
    /// pressing the second puts it away again. A fixed eye under a label that changed was the icon
    /// disagreeing with the word next to it every second press.
    /// </remarks>
    private const string RevealGlyph = "\uE890";
    private const string HideGlyph = "\uED1A";

    public event EventHandler<TranslateRequest>? TranslateRequested;
    public event EventHandler? OpenWindowRequested;
    public event EventHandler? CopyScreenshotRequested;
    public event EventHandler? CloseAllRequested;
    public event EventHandler<bool>? BubblesVisibilityChanged;

    /// <summary>The speak button was pressed: start reading, or stop if already reading.</summary>
    public event EventHandler? SpeakToggleRequested;

    /// <summary>
    /// Raised when the button that stops playback is about to stop being usable, so whoever owns the
    /// voice can stop it. Without this, switching the source language to 自動 mid-sentence would
    /// leave the text playing with no way to stop it — the stop button is the one being disabled.
    /// </summary>
    public event EventHandler? SpeakStopRequested;

    // Not readonly: the selection can still be moved and resized until translation starts, and this
    // toolbar is anchored to it — see FollowSelection.
    private double _selPhysLeft;
    private double _selPhysTop;
    private double _selPhysWidth;
    private double _selPhysHeight;

    private bool _isBusy        = false;
    private bool _toggleEnabled = false;
    private bool _bubblesVisible = true;
    private bool _hasTranslated;

    // Whether there is recognised text to read, and whether it is being read right now. The voice
    // itself lives with the capture session, not here: this window only shows its state.
    private bool _hasSpeakableText;
    private bool _isSpeaking;

    public string CurrentSourceLang => LanguageData.GetValidOcrSourceCode(SrcLangBox.SelectedValue as string);
    public string CurrentTargetLang => LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);

    public ToolbarWindow(
        double selPhysLeft, double selPhysTop,
        double selPhysWidth, double selPhysHeight,
        string sourceLang, string targetLang)
    {
        _selPhysLeft   = selPhysLeft;
        _selPhysTop    = selPhysTop;
        _selPhysWidth  = selPhysWidth;
        _selPhysHeight = selPhysHeight;

        InitializeComponent();
        InitializeSelectors(sourceLang, targetLang);

        // Attach after initial values are set so initialization doesn't trigger a save
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;

        RenderSpeakButton();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PositionNearSelection();

        // Re-applied once the window has landed: crossing to a monitor at another scale makes WPF
        // resize the window and Windows offer a replacement position, either of which moves the
        // edge just aligned to the selection. Same inputs, so it is a no-op on a uniform desktop.
        Dispatcher.BeginInvoke(new Action(PositionNearSelection), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-anchors the toolbar to the selection after the user has moved or resized it.
    /// </summary>
    /// <remarks>
    /// The same placement the toolbar opened with, run again: it stays under the box where there is
    /// room and flips above it where there is not, so dragging a selection down to the bottom of the
    /// screen moves the toolbar over the top of it rather than off the desktop.
    /// </remarks>
    public void FollowSelection(Rect physicalSelection)
    {
        _selPhysLeft   = physicalSelection.Left;
        _selPhysTop    = physicalSelection.Top;
        _selPhysWidth  = physicalSelection.Width;
        _selPhysHeight = physicalSelection.Height;

        // Nothing to place onto until the window has a handle; OnSourceInitialized does it then.
        if (IsLoaded) PositionNearSelection();
    }

    private void PositionNearSelection()
    {
        UpdateLayout();

        // All physical pixels, scaled by the monitor the selection is on. Deriving the scale from
        // this window instead reads whichever monitor WPF created it on: a toolbar measured at 96
        // DPI and then placed onto a 144 DPI monitor lands a factor of 1.5 from the selection.
        int centreX = (int)(_selPhysLeft + _selPhysWidth  / 2);
        int centreY = (int)(_selPhysTop  + _selPhysHeight / 2);
        double scale = ScreenGeometry.ScaleAt(centreX, centreY);

        // WPF lays out in DIP regardless of DPI, so the DIP size scales straight to target pixels.
        double tbW = (ActualWidth  > 0 ? ActualWidth  : 490) * scale;
        double tbH = (ActualHeight > 0 ? ActualHeight : 38)  * scale;

        var wa = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point(centreX, centreY)).WorkingArea;
        double margin = 4 * scale;
        double gap    = 6 * scale;

        // Math.Clamp throws when the toolbar is wider than the monitor it must fit on.
        double minLeft = wa.Left + margin;
        double maxLeft = Math.Max(minLeft, wa.Right - tbW - margin);
        double left = Math.Clamp(_selPhysLeft + (_selPhysWidth - tbW) / 2, minLeft, maxLeft);

        double yBelow = _selPhysTop + _selPhysHeight + gap;
        double yAbove = _selPhysTop - tbH - gap;

        double top;
        if (yBelow + tbH <= wa.Bottom)
            top = yBelow;
        else if (yAbove >= wa.Top)
            top = yAbove;
        else
            top = _selPhysTop + _selPhysHeight - tbH - 2 * scale;

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(left), (int)Math.Round(top));
    }

    private void SrcLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();

        // The source language is what decides whether there is a voice to read with — see
        // RenderSpeakButton.
        RenderSpeakButton();
    }

    private void TgtLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        SaveProviderSelection(provider);
    }

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var srcVal = SrcLangBox.SelectedValue as string;
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Target → Source: use explicit language mapping (e.g. ZH-HANT stays traditional)
        if (tgtVal != null)
        {
            var sourceCode = LanguageData.MapTargetToSourceCode(tgtVal);
            SrcLangBox.SelectedValue = sourceCode;
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Source → Target: use explicit language mapping
        if (srcVal != null)
        {
            var targetCode = LanguageData.MapSourceToTargetCode(srcVal);
            TgtLangBox.SelectedValue = targetCode;
        }
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;
    }

    private void TranslateBtn_Click(object sender, RoutedEventArgs e)
        => RequestTranslate();

    /// <summary>
    /// Fires the same request the 翻譯 button does, so auto-translate goes through the identical
    /// path (current selector values, busy state, re-translate labelling). Ignored while a batch
    /// is already running.
    /// </summary>
    public void RequestTranslate()
    {
        if (_isBusy) return;
        TranslateRequested?.Invoke(this, new TranslateRequest(CurrentSourceLang, CurrentTargetLang));
    }

    private void OpenWindowBtn_Click(object sender, RoutedEventArgs e)
        => OpenWindowRequested?.Invoke(this, EventArgs.Empty);

    private void TtsBtn_Click(object sender, RoutedEventArgs e)
        => SpeakToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Puts the toggle's icon and label on the same side of what pressing it would do.</summary>
    private void RenderToggleButton()
    {
        ToggleGlyph.Text = _bubblesVisible ? RevealGlyph : HideGlyph;
        ToggleLabel.Text = LocalizationService.Get(
            _bubblesVisible ? "S.Toolbar.ShowSource" : "S.Toolbar.ShowTranslation");
    }

    private void CopyShotBtn_Click(object sender, RoutedEventArgs e)
        => CopyScreenshotRequested?.Invoke(this, EventArgs.Empty);

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => CloseAllRequested?.Invoke(this, EventArgs.Empty);

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    public void SetBusy(bool busy)
    {
        _isBusy = busy;
        if (busy) HideEngineBadge(); // stale badge shouldn't linger while the next batch runs
        TranslateBtn.IsEnabled = !busy;
        TranslateLabel.Text = LocalizationService.Get(
            busy ? "S.Toolbar.Translating"
                 : _hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate");
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        OpenWindowBtn.IsEnabled = !_isBusy;
    }

    /// <summary>Whether recognition has produced any text for the speak button to read.</summary>
    public void SetSpeakableText(bool hasText)
    {
        _hasSpeakableText = hasText;
        RenderSpeakButton();
    }

    /// <summary>Reflects whether the voice is currently reading, so the button offers to stop it.</summary>
    public void SetSpeaking(bool speaking)
    {
        _isSpeaking = speaking;
        RenderSpeakButton();
    }

    /// <summary>
    /// Settles the speak button against what there is to read and what there is to read it with.
    /// </summary>
    /// <remarks>
    /// <para>Switched off while the source language is 自動, the same way the translation page's
    /// 原文 speaker is: there is no such thing as an automatic voice. <see cref="TtsService"/> maps
    /// 自動 onto Chinese, so English or Japanese text would be read aloud in a Chinese voice — which
    /// used to happen silently, leaving the user to work out from the sound that a picker three
    /// controls away was the cause. Recognition can run on 自動 because it is choosing between
    /// three scripts it can see; a voice has nothing to look at.</para>
    ///
    /// <para>Also off until recognition has produced something, because until then it is a button
    /// whose whole action is to do nothing.</para>
    ///
    /// <para>Not switched off while a translation is in flight, unlike everything else on this bar:
    /// the text being read is the one already recognised, the new batch does not touch it, and the
    /// button is also the only way to stop playback that is already running.</para>
    /// </remarks>
    private void RenderSpeakButton()
    {
        var automatic = LanguageData.IsAutomaticSource(SrcLangBox.SelectedValue as string);

        // Before the button goes dead: it is the only thing that can stop what it started.
        if (automatic && _isSpeaking) SpeakStopRequested?.Invoke(this, EventArgs.Empty);

        TtsBtn.IsEnabled = _hasSpeakableText && !automatic;

        // The glyph and the word are what pressing it does, the way the realtime bar's pause
        // button works. Both, not just the glyph: a stop square under the word 朗讀 says two
        // different things at once. Two characters either way, so the row does not shift.
        TtsGlyph.Text = _isSpeaking ? StopGlyph : SpeakGlyph;
        TtsLabel.Text = LocalizationService.Get(_isSpeaking ? "S.Toolbar.SpeakStopLabel" : "S.Toolbar.Speak");

        // Read through the service rather than bound in XAML, because which of the three applies is
        // a state and not a constant.
        TtsBtn.ToolTip = LocalizationService.Get(
            automatic ? "S.Toolbar.SpeakAutomatic"
                      : !_hasSpeakableText ? "S.Toolbar.SpeakNoText"
                      : _isSpeaking ? "S.Toolbar.SpeakStop"
                      : "S.Toolbar.SpeakHint");
    }

    public void SetTranslationState(bool hasTranslated)
    {
        _hasTranslated = hasTranslated;
        if (!_isBusy)
            TranslateLabel.Text = LocalizationService.Get(
                hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate");
    }

    /// <summary>
    /// Shows a subtle amber badge naming the engine that actually served the batch — but only when a
    /// backup engine was used (the user's chosen primary couldn't serve everything). Stays hidden on
    /// normal runs and for providers without fallback (e.g. DeepL), so it never nags during use.
    /// </summary>
    public void SetEngineBadge(EngineUsage? usage)
    {
        if (usage is null || !usage.FallbackUsed)
        {
            HideEngineBadge();
            return;
        }

        EngineBadgeText.Text = LocalizationService.Format("S.Toolbar.BackupBadge", usage.BackupEngine);
        EngineBadge.ToolTip = LocalizationService.Format(
            "S.Toolbar.BackupTooltip", usage.Primary, usage.BackupEngine, usage.Summary);
        EngineBadge.Visibility = Visibility.Visible;
    }

    private void HideEngineBadge()
    {
        EngineBadge.Visibility = Visibility.Collapsed;
        EngineBadge.ToolTip    = null;
    }

    public void SetToggleEnabled(bool enabled)
    {
        _toggleEnabled   = enabled;
        _bubblesVisible  = true;
        RenderToggleButton();
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _bubblesVisible = !_bubblesVisible;
        RenderToggleButton();
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void InitializeSelectors(string sourceLang, string targetLang)
    {
        SrcLangBox.ItemsSource  = LanguageData.OcrSourceLanguages;
        TgtLangBox.ItemsSource  = LanguageData.TargetLanguages;
        ProviderBox.ItemsSource = LanguageData.Providers;

        SrcLangBox.SelectedValue  = LanguageData.GetValidOcrSourceCode(sourceLang);
        TgtLangBox.SelectedValue  = LanguageData.GetValidTargetCode(targetLang);
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
    }

    private void SaveCurrentLanguageSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = CurrentSourceLang;
        settings.TargetLanguage = CurrentTargetLang;
        SettingsService.Instance.Save();
    }

    private static void SaveProviderSelection(TranslationProvider provider)
    {
        var settings = SettingsService.Instance.Current;
        settings.Provider = provider;
        SettingsService.Instance.Save();
    }
}

public record TranslateRequest(string SourceLang, string TargetLang);
