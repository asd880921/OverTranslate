using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace OverTranslate;

public partial class AboutWindow : Window
{
    private static AboutWindow? _instance;
    private const string GitHubUrl = "https://github.com/asd880921/OverTranslate";

    public static void ShowOrActivate()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new AboutWindow();
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
    }

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"版本 {version}";
    }

    private void GitHubBtn_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
    }
}
