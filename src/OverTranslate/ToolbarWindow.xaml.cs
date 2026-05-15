using System.Windows;
using System.Windows.Interop;
using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate;

public partial class ToolbarWindow : Window
{
    public event EventHandler<TranslateRequest>? TranslateRequested;
    public event EventHandler? OpenWindowRequested;
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
    }

    private void PositionNearSelection()
    {
        var src = PresentationSource.FromVisual(this);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double selLeft = _selPhysLeft / dpiX;
        double selTop  = _selPhysTop  / dpiY;
        double selW    = _selPhysWidth  / dpiX;
        double selH    = _selPhysHeight / dpiY;

        UpdateLayout();
        double tbW = ActualWidth  > 0 ? ActualWidth  : 490;
        double tbH = ActualHeight > 0 ? ActualHeight : 38;

        double screenRight  = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = SystemParameters.VirtualScreenTop  + SystemParameters.VirtualScreenHeight;

        double left = selLeft + (selW - tbW) / 2;
        left = Math.Clamp(left, SystemParameters.VirtualScreenLeft + 4, screenRight - tbW - 4);

        const double gap = 6;
        double yBelow = selTop + selH + gap;
        double yAbove = selTop - tbH - gap;

        double top;
        if (yBelow + tbH <= screenBottom)
            top = yBelow;
        else if (yAbove >= SystemParameters.VirtualScreenTop)
            top = yAbove;
        else
            top = selTop + selH - tbH - 2;

        Left = left;
        Top  = top;
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
        => TranslateRequested?.Invoke(this, new TranslateRequest(CurrentSourceLang, CurrentTargetLang));

    private void OpenWindowBtn_Click(object sender, RoutedEventArgs e)
        => OpenWindowRequested?.Invoke(this, EventArgs.Empty);

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
        TranslateBtn.IsEnabled = !busy;
        TranslateBtn.Content   = busy ? "翻譯中..." : (_hasTranslated ? "重新翻譯" : "翻譯");
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        OpenWindowBtn.IsEnabled = !_isBusy;
    }

    public void SetTranslationState(bool hasTranslated)
    {
        _hasTranslated = hasTranslated;
        if (!_isBusy)
            TranslateBtn.Content = hasTranslated ? "重新翻譯" : "翻譯";
    }

    public void SetToggleEnabled(bool enabled)
    {
        _toggleEnabled   = enabled;
        _bubblesVisible  = true;
        ToggleBtn.Content   = "顯示原文";
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _bubblesVisible   = !_bubblesVisible;
        ToggleBtn.Content = _bubblesVisible ? "顯示原文" : "顯示譯文";
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
