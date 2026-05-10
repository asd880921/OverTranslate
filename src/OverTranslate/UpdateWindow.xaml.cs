using System.Diagnostics;
using System.Reflection;
using System.Windows;
using OverTranslate.Services;

namespace OverTranslate;

public partial class UpdateWindow : Window
{
    private readonly string _releaseUrl;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _releaseUrl = info.ReleaseUrl;

        var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        CurrentVersionText.Text = current;
        LatestVersionText.Text  = info.LatestVersion.ToString(3);
    }

    private void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        Close();
    }

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Close();
}
