using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Shell;

/// <summary>
/// Modal "about" panel drawn on top of the shell instead of in its own window, so the app
/// keeps a single visible surface. Dismissed by the close button, the scrim, or Escape.
/// </summary>
public partial class AboutOverlay : UserControl
{
    private const string GitHubUrl = "https://github.com/asd880921/OverTranslate";

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    public AboutOverlay()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"版本 {version}";
    }

    public void Open()
    {
        Visibility = Visibility.Visible;

        // Fade only. The card used to also scale up from 0.96, but WPF abandons pixel-aligned text
        // rendering for anything animating under a RenderTransform, and scaling is the worst case:
        // every glyph is resampled at a slightly different size each frame. The card's text was
        // visibly soft until the animation ended and then snapped sharp. Alpha alone leaves every
        // glyph on its exact final pixel throughout.
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);

        // Focus lets the control receive Escape without the page underneath stealing it
        Focus();
    }

    public void Close()
    {
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
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

    private void GitHubBtn_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
}
