using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
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
/// The page holds two kinds of value, and the line between them is the rule to keep:
///
/// <para><b>Parameters of one sitting</b> — screen, block count, languages, service — are never read
/// from or written to the settings file. Reopening the window offers the defaults again rather than
/// restoring a set-up whose screen may since have been unplugged and whose blocks are long gone.
/// That includes the translation service, which the rest of the application does share as one
/// preference: watching a game and translating a document are different jobs with different
/// tolerances — one wants an engine that answers in 80ms, the other one that reads carefully — and
/// having this page quietly change what 設定 shows was the worse of the two surprises.</para>
///
/// <para><b>Appearance preferences</b> — the subtitle's two colours, in 顯示外觀 — are stored and
/// restored. A reader who needs yellow on dark blue needs it every session, and re-picking it each
/// time is the same failure as forgetting a theme. They live here rather than in 設定 so the control
/// sits beside what it changes.</para>
///
/// <para>Nothing on screen currently marks which card is which; 翻譯區塊 states that its value is not
/// kept, and 顯示外觀 says nothing either way. That is a deliberate trade for a shorter card, not an
/// oversight — if it turns out to confuse anyone, the fix is a line in 顯示外觀 saying its values are
/// kept, not removing the note from 翻譯區塊.</para>
///
/// <para><b>Kept for as long as this window is open</b> — the blocks the user drew, with the mode
/// chosen for each. Not a preference and not a parameter: it is the answer to "where is the text",
/// which is worth several minutes of dragging and is thrown away the moment the question changes.
/// It lives in <see cref="RealtimeSessionController"/> rather than here, and this page tells it to
/// forget in the two cases that change the question — the block count moving, and this window
/// closing.</para>
///
/// Anything added later belongs to one of these three groups; if it is not obvious which, it is a
/// preference only when the user would be annoyed to set it twice, and a parameter only when
/// offering it back could be wrong rather than merely stale.
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

        SrcLangBox.ItemsSource = LanguageData.OcrSourceLanguages
            .Where(language => !LanguageData.IsAutomaticSource(language.Code));
        TgtLangBox.ItemsSource = LanguageData.TargetLanguages;

        ProviderBox.ItemsSource = LanguageData.Providers;

        ApplyPageDefaults();

        // Attached after the initial value is set, so initialisation does not fire the handler
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
        SrcLangBox.SelectionChanged += (_, _) => RenderState();

        RenderColours();
        RenderRefreshHint();

        RealtimeSessionController.Instance.StateChanged += OnSessionStateChanged;

        // Not just the stepper: the start button has to begin unavailable too, because 原文語言
        // starts unset and RenderState is what ties the two together.
        RenderState();
    }

    /// <summary>
    /// This page's own starting point, independent of what is saved.
    /// </summary>
    /// <remarks>
    /// 原文語言 is left unset on purpose. Realtime recognition offers no retry once the frame has
    /// passed, so the user has to make the source language explicit before starting a session. An
    /// empty field asks that question instead of silently choosing an unreliable automatic mode.
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
        RenderColours();
        RenderRefreshHint();
        RenderState();
    }

    /// <summary>
    /// Names the capture shortcut in 運作方式, or drops the line when there is none to name.
    /// </summary>
    /// <remarks>
    /// Re-read on every <see cref="Reload"/> rather than only at construction: 設定 is where that
    /// shortcut is changed, and the user reaches this page again through the same nav rail — so the
    /// line would otherwise go on naming the key they just replaced.
    /// </remarks>
    private void RenderRefreshHint()
    {
        RefreshHint.Text = RealtimeRefreshHint.ForSettingsPage(RealtimeRefreshHint.CurrentHotkey);
        RefreshHint.Visibility = RefreshHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Detaches from the controller when the shell window is destroyed.</summary>
    /// <remarks>
    /// The kept block layout goes with the window, which is the whole of its scope: it exists so
    /// that ending a session to change something on this page does not cost the user their blocks,
    /// and closing the window is the end of that errand. A session already running is left alone —
    /// it owns its own blocks, and closing this window has never been a request to stop it.
    /// </remarks>
    public void Teardown()
    {
        RealtimeSessionController.Instance.StateChanged -= OnSessionStateChanged;
        RealtimeSessionController.Instance.ForgetBlocks();
    }

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
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }

    private void LoadScreens()
    {
        var previous = ScreenBox.SelectedValue as string;

        var screens = Screen.AllScreens;
        var items = screens
            .Select((screen, index) => new ScreenItem(
                screen.DeviceName,
                $"螢幕 {index + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? "（主螢幕）" : "")}",
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

        var settings = SettingsService.Instance.Current;
        var request = new RealtimeStartRequest(
            screen.Bounds,
            _blockCount,
            LanguageData.GetValidOcrSourceCode(sourceLanguage),
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string),
            ProviderBox.SelectedValue as TranslationProvider? ?? DefaultProvider,
            settings.RealtimeTextColor,
            settings.RealtimeScrimColor);

        // The shell is handed over to be hidden: it is almost certainly sitting on the screen the
        // user is about to frame blocks on, and it comes back when the session ends.
        controller.Start(request, Window.GetWindow(this));
    }

    // ── 顯示外觀 ───────────────────────────────────────────────────────────────

    private void TextColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.RealtimeTextColor,
            picked => Persist(s => s.RealtimeTextColor = picked));

    private void ScrimColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.RealtimeScrimColor,
            picked => Persist(s => s.RealtimeScrimColor = picked));

    private void ResetColorsBtn_Click(object sender, RoutedEventArgs e) =>
        Persist(s =>
        {
            s.RealtimeTextColor = RealtimeSubtitleColors.DefaultText;
            s.RealtimeScrimColor = RealtimeSubtitleColors.DefaultScrim;
        });

    /// <summary>
    /// Opens the system colour picker on the current value and reports what came back.
    /// </summary>
    /// <remarks>
    /// The native dialog, not one of ours: it already carries a full picker, the custom-colour slots
    /// and every keyboard convention, and none of that is worth rebuilding for two fields. It has no
    /// alpha channel, which suits a setting that has no alpha to offer — the scrim's is fixed, see
    /// <see cref="RealtimeSubtitleColors"/>.
    /// </remarks>
    private void PickColour(string current, Action<string> onPicked)
    {
        var start = RealtimeSubtitleColors.Text(current);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            // Opens on what is in effect, so a small adjustment starts from the current colour
            // rather than from black.
            Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
            FullOpen = true,
            AnyColor = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        onPicked(RealtimeSubtitleColors.Format(
            System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
    }

    private void Persist(Action<AppSettings> apply)
    {
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        RenderColours();
    }

    /// <summary>
    /// Paints the two swatches, their hex labels and the preview from what is currently stored.
    /// </summary>
    private void RenderColours()
    {
        var settings = SettingsService.Instance.Current;
        var text = RealtimeSubtitleColors.Text(settings.RealtimeTextColor);
        var scrim = RealtimeSubtitleColors.Scrim(settings.RealtimeScrimColor);

        // The swatch shows the colour the user picked, so it is drawn opaque even for the scrim —
        // the preview below is where its translucency is on display.
        TextColorSwatch.Background = new SolidColorBrush(text);
        ScrimColorSwatch.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(scrim.R, scrim.G, scrim.B));

        TextColorValue.Text = RealtimeSubtitleColors.Format(text);
        ScrimColorValue.Text = RealtimeSubtitleColors.Format(scrim);

        PreviewText.Foreground = new SolidColorBrush(text);
        PreviewScrim.Background = new SolidColorBrush(scrim);

        ResetColorsBtn.IsEnabled =
            settings.RealtimeTextColor != RealtimeSubtitleColors.DefaultText ||
            settings.RealtimeScrimColor != RealtimeSubtitleColors.DefaultScrim;
    }

    private void BlockCountDown_Click(object sender, RoutedEventArgs e) => StepBlockCount(-1);

    private void BlockCountUp_Click(object sender, RoutedEventArgs e) => StepBlockCount(+1);

    private void StepBlockCount(int delta)
    {
        var next = Math.Clamp(_blockCount + delta, MinBlocks, MaxBlocks);
        if (next == _blockCount) return;

        _blockCount = next;

        // The blocks kept from the last sitting were drawn to fill a different count, so they are
        // no longer an answer to the question being asked. Dropped as the count moves rather than
        // checked when they are offered back, so the user sees the rule they were given.
        RealtimeSessionController.Instance.ForgetBlocks();

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
        // rather than by refusing after the fact, and the field keeps its prompt until answered.
        var hasSource = SrcLangBox.SelectedValue is string;
        PrimaryBtn.IsEnabled = active || hasSource;
        SrcLangPlaceholder.Visibility = hasSource ? Visibility.Collapsed : Visibility.Visible;

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
