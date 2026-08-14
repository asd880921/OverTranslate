using System.Windows;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

namespace OverTranslate.Views.Capture;

public partial class ToolbarWindow : Window
{
    public event EventHandler<TranslateRequest>? TranslateRequested;
    public event EventHandler? OpenWindowRequested;
    public event EventHandler? CopyScreenshotRequested;
    public event EventHandler? CloseAllRequested;
    public event EventHandler<bool>? BubblesVisibilityChanged;

    private readonly double _selPhysLeft;
    private readonly double _selPhysTop;
    private readonly double _selPhysWidth;
    private readonly double _selPhysHeight;

    private bool _isBusy        = false;
    private bool _toggleEnabled = false;
    private bool _bubblesVisible = true;
    private bool _hasTranslated;

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
        TranslateBtn.Content = LocalizationService.Get(
            busy ? "S.Toolbar.Translating"
                 : _hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate");
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        OpenWindowBtn.IsEnabled = !_isBusy;
    }

    public void SetTranslationState(bool hasTranslated)
    {
        _hasTranslated = hasTranslated;
        if (!_isBusy)
            TranslateBtn.Content = LocalizationService.Get(
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
        ToggleBtn.Content = LocalizationService.Get("S.Toolbar.ShowSource");
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _bubblesVisible   = !_bubblesVisible;
        ToggleBtn.Content = LocalizationService.Get(
            _bubblesVisible ? "S.Toolbar.ShowSource" : "S.Toolbar.ShowTranslation");
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
