using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Translation;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so this name collides
using Point = System.Windows.Point;

namespace OverTranslate.Views.Shell;

public enum ShellPage
{
    Translation,
    Settings
}

/// <summary>
/// The application's single operable window: a left nav rail plus a content area that swaps
/// between pages. Every entry point (tray icon, tray menu, capture toolbar, second launch)
/// funnels through <see cref="ShowOrActivate"/> so only one of these ever exists.
/// </summary>
public partial class ShellWindow : Window
{
    private static ShellWindow? _instance;

    private static readonly Duration IndicatorDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration ContentDuration   = new(TimeSpan.FromMilliseconds(120));

    // Token for the in-flight content transition; see AnimateContentIn.
    private object? _contentTransition;

    private readonly TranslationPage _translationPage = new();
    private readonly SettingsPage    _settingsPage    = new();

    private ShellPage? _current;

    public TranslationPage TranslationPage => _translationPage;

    /// <summary>
    /// Shows the shell on <paramref name="page"/>, creating it if needed, and returns the
    /// live instance so callers can push content into a page.
    /// </summary>
    public static ShellWindow ShowOrActivate(ShellPage page = ShellPage.Translation)
    {
        if (_instance == null)
        {
            _instance = new ShellWindow();
            _instance.Show();
        }
        else if (_instance.WindowState == WindowState.Minimized)
        {
            _instance.WindowState = WindowState.Normal;
        }

        _instance.Navigate(page);
        _instance.RefreshHotkeyHint();

        var shell = _instance;
        shell.Dispatcher.BeginInvoke(shell.Activate, DispatcherPriority.ApplicationIdle);
        return shell;
    }

    public ShellWindow()
    {
        InitializeComponent();

        var icon = AppIconService.CreateWindowIcon();
        Icon = icon;
        BrandIcon.Source = icon;
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";

        _instance = this;
        RefreshHotkeyHint();

        // Nav_Checked drives navigation, so this also renders the initial page
        TranslationNav.IsChecked = true;
    }

    /// <summary>
    /// Re-reads the shortcut into the capture button's second line. The shell outlives any number
    /// of visits to 設定, so this runs on every ShowOrActivate rather than only at construction.
    /// </summary>
    private void RefreshHotkeyHint()
    {
        var hotkey = SettingsService.Instance.Current.HotkeyDisplay;
        // Blank rather than a "未設定" placeholder: the label already says what the button does,
        // and a slot that only ever holds a shortcut should be empty when there is none.
        CaptureHotkeyText.Text = string.IsNullOrWhiteSpace(hotkey) ? "" : hotkey;
    }

    private void CaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow owns the whole capture session (hotkey, screenshot, overlay, teardown), so
        // the shell only asks for one and hands itself over to be hidden and brought back.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartCaptureFromShell(this);
    }

    public void Navigate(ShellPage page)
    {
        var target = page == ShellPage.Settings ? SettingsNav : TranslationNav;
        if (target.IsChecked == true)
        {
            // Already here — still make sure the content is mounted (first call from the ctor)
            if (_current != page) ShowPage(page);
            return;
        }
        target.IsChecked = true;   // raises Nav_Checked
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        var page = ReferenceEquals(sender, SettingsNav) ? ShellPage.Settings : ShellPage.Translation;
        if (_current == page) return;
        ShowPage(page);
    }

    private void ShowPage(ShellPage page)
    {
        _current = page;

        if (page == ShellPage.Settings) _settingsPage.Reload();
        ContentHost.Child = page == ShellPage.Settings ? _settingsPage : (UIElement)_translationPage;

        MoveIndicatorTo(page == ShellPage.Settings ? SettingsNav : TranslationNav);
        AnimateContentIn();
    }

    /// <summary>
    /// Slides the accent bar to the selected item. The offset is measured from the live layout
    /// rather than assumed from an item height, so it stays correct if the nav gains items.
    /// </summary>
    private void MoveIndicatorTo(FrameworkElement item)
    {
        if (!item.IsLoaded)
        {
            // First navigation happens before layout — retry once the nav has been measured
            item.Loaded += OnceLoaded;
            return;
        }

        var top = item.TransformToAncestor(NavPanel).Transform(new Point(0, 0)).Y;
        var to  = top + (item.ActualHeight - NavIndicator.Height) / 2;

        NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = to,
            Duration = IndicatorDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        void OnceLoaded(object? s, RoutedEventArgs e)
        {
            item.Loaded -= OnceLoaded;
            MoveIndicatorTo(item);
        }
    }

    private void AnimateContentIn()
    {
        // Identifies this particular transition, so a fast page switch cannot have the previous
        // animation's completion handler tear down the animation that replaced it.
        var transition = new object();
        _contentTransition = transition;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated or
        // scrolled, then ramps snapping back on over roughly a second once the motion stops. That
        // ramp is why the page used to sit visibly settled and stay soft for a beat before turning
        // sharp, and there is no API to shorten or disable it. Rendering the page into a bitmap
        // cache for the duration of the slide sidesteps the whole mechanism: the glyphs are
        // rasterised once as static, snapped text, and the render thread then only moves the
        // finished bitmap, so nothing ever looks like animating text to the detector.
        // SnapsToDevicePixels keeps that bitmap on whole pixels as it moves, so no frame of the
        // slide is resampled either. The cache is dropped again in ReleaseContentAnimations.
        ContentHost.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        // Both animations run for the same duration, so one Completed handler covers the pair.
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = ContentDuration };
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_contentTransition, transition)) return;
            ReleaseContentAnimations();
        };

        ContentHost.BeginAnimation(OpacityProperty, fade);
        ContentTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 8, To = 0,
            Duration = ContentDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // DoubleAnimation defaults to FillBehavior.HoldEnd, which keeps the animated properties under
    // the animation clock's control even after the animation has visually finished, holding the
    // content in an intermediate composition layer indefinitely. Handing the properties back to the
    // elements drops that layer the moment the transition ends, so the settled page renders exactly
    // as it would have if it had never animated.
    private void ReleaseContentAnimations()
    {
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = 1;

        ContentTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ContentTransform.Y = 0;

        // Back to rendering the live visual tree — the cache existed only for the slide, and the
        // settled page has to be real text again for selection, scrolling and DPI changes.
        ContentHost.CacheMode = null;
    }

    private void AboutBtn_Click(object sender, RoutedEventArgs e) => About.Open();

    protected override void OnClosed(EventArgs e)
    {
        // The window is destroyed on close (not hidden), so the pages' timers, TTS playback
        // and HTTP handles have to go with it.
        _translationPage.Teardown();
        _instance = null;
        base.OnClosed(e);
    }
}
