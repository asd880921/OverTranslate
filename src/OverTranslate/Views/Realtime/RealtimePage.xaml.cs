using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OverTranslate.Models;
using OverTranslate.Services;
using Screen = System.Windows.Forms.Screen;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>One monitor, as offered in the screen picker.</summary>
public sealed record ScreenItem(
    string DeviceName, string Display, System.Drawing.Rectangle Bounds, bool IsPrimary);

/// <summary>
/// Sets up a realtime session and then gets out of the way — everything after 選取翻譯區塊 happens on
/// the screen itself, driven by <see cref="RealtimeSessionController"/>.
/// </summary>
/// <remarks>
/// Nothing on this page is read from or written to the settings file, deliberately: every field
/// here is a parameter of one sitting — which screen, how many areas, which languages — not a
/// preference. Reopening the window offers the defaults again rather than restoring a set-up whose
/// screen may since have been unplugged and whose blocks are long gone.
///
/// That includes the translation service, which the rest of the application does share as one
/// preference. Watching a game and translating a document are different jobs with different
/// tolerances — one wants an engine that answers in 80ms, the other one that reads carefully — and
/// having this page quietly change what 設定 shows was the worse of the two surprises.
/// </remarks>
public partial class RealtimePage : UserControl
{
    /// <summary>
    /// 繁體中文, because this page's own reason to exist is watching foreign content, and the reader
    /// is the person sitting here.
    /// </summary>
    private const string DefaultTargetLanguage = "ZH-HANT";

    /// <summary>
    /// Microsoft, chosen for this page rather than inherited: measured over 639 realtime passes it
    /// answered in 82ms at the median, which is what makes a subtitle appear to keep up. A service
    /// that reads more carefully but takes a second is the right default elsewhere and the wrong one
    /// here.
    /// </summary>
    private const TranslationProvider DefaultProvider = TranslationProvider.Microsoft;

    private const int MinBlocks = 1;
    private const int MaxBlocks = 3;

    private int _blockCount = MinBlocks;

    public RealtimePage()
    {
        InitializeComponent();

        SrcLangBox.ItemsSource = LanguageData.OcrSourceLanguages;
        TgtLangBox.ItemsSource = LanguageData.TargetLanguages;

        ProviderBox.ItemsSource = LanguageData.Providers;

        ApplyPageDefaults();

        // Attached after the initial value is set, so initialisation does not fire the handler
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
        SrcLangBox.SelectionChanged += (_, _) => RenderState();

        RealtimeSessionController.Instance.StateChanged += OnSessionStateChanged;

        // Not just the stepper: the start button has to begin unavailable too, because 原文語言
        // starts unset and RenderState is what ties the two together.
        RenderState();
    }

    /// <summary>
    /// This page's own starting point, independent of what is saved.
    /// </summary>
    /// <remarks>
    /// 原文語言 is left unset on purpose. Recognition needs to be told which script to read — it
    /// cannot be inferred from the pixels the way a translator infers a language from words — and a
    /// wrong one does not fail loudly, it returns plausible nonsense. An empty field asks the one
    /// question the feature cannot answer for itself; a pre-filled one invites the user straight
    /// past it.
    /// </remarks>
    private void ApplyPageDefaults()
    {
        SrcLangBox.SelectedIndex = -1;
        TgtLangBox.SelectedValue = DefaultTargetLanguage;
        ProviderBox.SelectedValue = DefaultProvider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        RenderProviderHint();
    }

    /// <summary>
    /// Re-reads what may have changed while the user was elsewhere: the attached monitors, and
    /// whether a session is running.
    /// </summary>
    public void Reload()
    {
        LoadScreens();
        RenderState();
    }

    /// <summary>Detaches from the controller when the shell window is destroyed.</summary>
    public void Teardown() => RealtimeSessionController.Instance.StateChanged -= OnSessionStateChanged;

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderProviderHint();

    /// <summary>
    /// The service's own note, plus the one thing that would otherwise only surface as a failure
    /// mid-session: a key-based service with no key saved. Said here, before the screen is handed
    /// over, rather than as an error on a floating bar the user is no longer looking at.
    /// </summary>
    private void RenderProviderHint()
    {
        var item = ProviderBox.SelectedItem as ProviderItem;
        var missingKey = item?.RequiresApiKey == true &&
                         string.IsNullOrWhiteSpace(SettingsService.Instance.Current.ApiKey);

        ProviderHint.Text = missingKey
            ? "尚未設定 API Key，請先到「設定」輸入，否則即時翻譯會無法取得譯文。"
            : item?.Hint ?? "";
        ProviderHint.Foreground = missingKey
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextMuted");
    }

    private void LoadScreens()
    {
        var previous = ScreenBox.SelectedValue as string;

        var screens = Screen.AllScreens;
        var items = screens
            .Select((screen, index) => new ScreenItem(
                screen.DeviceName,
                $"螢幕 {index + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? "（主要）" : "")}",
                screen.Bounds,
                screen.Primary))
            .ToList();

        ScreenBox.ItemsSource = items;
        ScreenBox.DisplayMemberPath = nameof(ScreenItem.Display);

        // Keep the user's pick across a reload, but fall back to the monitor this window is on —
        // which is the one they are looking at, and not necessarily the primary.
        if (previous != null && items.Any(item => item.DeviceName == previous))
            ScreenBox.SelectedValue = previous;
        else
            ScreenBox.SelectedValue = CurrentScreenDeviceName(items);
    }

    private string? CurrentScreenDeviceName(IReadOnlyList<ScreenItem> items)
    {
        try
        {
            if (Window.GetWindow(this) is { } window)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    var deviceName = Screen.FromHandle(handle).DeviceName;
                    if (items.Any(item => item.DeviceName == deviceName))
                        return deviceName;
                }
            }
        }
        catch (Exception)
        {
            // Window not yet sourced, or a monitor removed mid-call — the primary below is a fine
            // answer either way.
        }

        return items.FirstOrDefault(item => item.IsPrimary)?.DeviceName
            ?? items.FirstOrDefault()?.DeviceName;
    }

    private void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        var controller = RealtimeSessionController.Instance;
        if (controller.IsActive)
        {
            controller.Stop();
            return;
        }

        // The other half of the same rule the capture side enforces: one OCR engine, one pool of
        // inference slots, and neither feature is any use with the other competing for them. This
        // way round is the less likely of the two — a capture covers the screen, so its selection
        // layer is usually over this button — but the toolbar phase leaves the screen usable.
        if (System.Windows.Application.Current.MainWindow is MainWindow { IsCapturing: true })
        {
            SetStatus("截圖翻譯進行中，請先結束後再啟動即時翻譯。", isError: true);
            return;
        }

        if (SrcLangBox.SelectedValue is not string sourceLanguage)
        {
            SetStatus("請先選擇原文語言。", isError: true);
            return;
        }

        if (ScreenBox.SelectedItem is not ScreenItem screen)
        {
            SetStatus("找不到可用的螢幕，請重新開啟此頁面。", isError: true);
            return;
        }

        var request = new RealtimeStartRequest(
            screen.Bounds,
            _blockCount,
            LanguageData.GetValidOcrSourceCode(sourceLanguage),
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string),
            ProviderBox.SelectedValue as TranslationProvider? ?? DefaultProvider);

        // The shell is handed over to be hidden: it is almost certainly sitting on the screen the
        // user is about to frame blocks on, and it comes back when the session ends.
        controller.Start(request, Window.GetWindow(this));
    }

    private void BlockCountDown_Click(object sender, RoutedEventArgs e) => StepBlockCount(-1);

    private void BlockCountUp_Click(object sender, RoutedEventArgs e) => StepBlockCount(+1);

    private void StepBlockCount(int delta)
    {
        var next = Math.Clamp(_blockCount + delta, MinBlocks, MaxBlocks);
        if (next == _blockCount) return;

        _blockCount = next;
        RenderBlockCount();
    }

    /// <summary>
    /// Shows the count and switches off whichever button would do nothing.
    /// </summary>
    /// <remarks>
    /// Disabled rather than silently ignored at the ends: a button that still looks pressable and
    /// then does nothing reads as the application having missed the click.
    /// </remarks>
    private void RenderBlockCount()
    {
        var active = RealtimeSessionController.Instance.IsActive;

        BlockCountText.Text = _blockCount.ToString();
        BlockCountDown.IsEnabled = !active && _blockCount > MinBlocks;
        BlockCountUp.IsEnabled = !active && _blockCount < MaxBlocks;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RenderState);

    private void RenderState()
    {
        bool active = RealtimeSessionController.Instance.IsActive;

        PrimaryBtn.Content = active ? "結束即時翻譯" : "選取翻譯區塊";

        // Locked rather than hidden while running: the user can see what the session was started
        // with, and changing any of it would mean rebuilding every block anyway.
        SrcLangBox.IsEnabled = !active;
        TgtLangBox.IsEnabled = !active;
        ProviderBox.IsEnabled = !active;
        ScreenBox.IsEnabled = !active;
        RenderBlockCount();

        // Nothing can start without a source language, so the button says so by being unavailable
        // rather than by refusing after the fact.
        PrimaryBtn.IsEnabled = active || SrcLangBox.SelectedValue is string;

        SetStatus(
            active ? "即時翻譯進行中，可用螢幕上的浮動列調整或結束。" : "",
            isError: false);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }
}
