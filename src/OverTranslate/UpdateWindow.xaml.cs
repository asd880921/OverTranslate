using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using OverTranslate.Services;

namespace OverTranslate;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _updateInfo;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _updateInfo = info;

        var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        CurrentVersionText.Text = current;
        LatestVersionText.Text  = info.LatestVersion;
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DismissBtn.IsEnabled = false;
            DownloadBtn.IsEnabled = false;
            DownloadBtnText.Text = "更新中...";
            await UpdateService.DownloadAndApplyAsync(_updateInfo);
        }
        catch (Exception ex)
        {
            DismissBtn.IsEnabled = true;
            DownloadBtn.IsEnabled = true;
            DownloadBtnText.Text = "立即更新";
            System.Windows.MessageBox.Show(
                this,
                $"下載更新失敗：{ex.Message}",
                "更新失敗",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ReleaseNotesLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
