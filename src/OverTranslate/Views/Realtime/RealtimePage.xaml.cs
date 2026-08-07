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
/// Nothing on this page is written to the settings file, deliberately: these are the parameters of
/// one sitting (which screen, how many areas), not preferences. Reopening the window offers the
/// defaults again rather than restoring a set-up whose screen may since have been unplugged and
/// whose blocks are long gone.
/// </remarks>
public partial class RealtimePage : UserControl
{
    public RealtimePage()
    {
        InitializeComponent();

        SrcLangBox.ItemsSource = LanguageData.OcrSourceLanguages;
        TgtLangBox.ItemsSource = LanguageData.TargetLanguages;

        ProviderBox.ItemsSource = LanguageData.Providers;

        var settings = SettingsService.Instance.Current;
        SrcLangBox.SelectedValue = LanguageData.GetValidOcrSourceCode(settings.SourceLanguage);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        LoadProvider();

        // Attached after the initial value is set, so initialisation does not write it straight back
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;

        RealtimeSessionController.Instance.StateChanged += OnSessionStateChanged;
    }

    /// <summary>
    /// Re-reads what may have changed while the user was elsewhere: the attached monitors, and
    /// whether a session is running.
    /// </summary>
    public void Reload()
    {
        LoadScreens();
        // The service is a shared preference, so 設定 may have changed it since this page was last on
        // screen. Re-read rather than let the two disagree.
        LoadProvider();
        RenderState();
    }

    /// <summary>Detaches from the controller when the shell window is destroyed.</summary>
    public void Teardown() => RealtimeSessionController.Instance.StateChanged -= OnSessionStateChanged;

    private void LoadProvider()
    {
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        RenderProviderHint();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;

        SettingsService.Instance.Current.Provider = provider;
        SettingsService.Instance.Save();
        RenderProviderHint();
    }

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

        if (ScreenBox.SelectedItem is not ScreenItem screen)
        {
            SetStatus("找不到可用的螢幕，請重新開啟此頁面。", isError: true);
            return;
        }

        var request = new RealtimeStartRequest(
            screen.Bounds,
            SelectedBlockLimit(),
            LanguageData.GetValidOcrSourceCode(SrcLangBox.SelectedValue as string),
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string));

        // The shell is handed over to be hidden: it is almost certainly sitting on the screen the
        // user is about to frame blocks on, and it comes back when the session ends.
        controller.Start(request, Window.GetWindow(this));
    }

    private int SelectedBlockLimit() =>
        Limit3.IsChecked == true ? 3 :
        Limit2.IsChecked == true ? 2 : 1;

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
        Limit1.IsEnabled = !active;
        Limit2.IsEnabled = !active;
        Limit3.IsEnabled = !active;

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
