using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace OverTranslate.Views.Shell;

public partial class AboutWindow : Window
{
    private static AboutWindow? _instance;
    private const string GitHubUrl = "https://github.com/asd880921/OverTranslate";

    public static void ShowOrActivate(Window? owner = null)
    {
        if (_instance != null)
        {
            if (owner != null && !ReferenceEquals(_instance.Owner, owner))
                _instance.Owner = owner;
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new AboutWindow();
        if (owner != null)
            _instance.Owner = owner;
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Dispatcher.BeginInvoke(() => _instance.Activate(), DispatcherPriority.ApplicationIdle);
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
