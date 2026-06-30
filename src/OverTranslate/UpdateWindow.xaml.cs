using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using OverTranslate.Services;

namespace OverTranslate;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _updateInfo;
    private bool _isUpdating;

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
            _isUpdating = true;
            DismissBtn.IsEnabled = false;
            DownloadBtn.IsEnabled = false;
            DownloadBtnText.Text = "下載中 0%";
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Visible;

            await UpdateService.DownloadAndApplyAsync(_updateInfo, OnDownloadProgress);
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            DismissBtn.IsEnabled = true;
            DownloadBtn.IsEnabled = true;
            DownloadBtnText.Text = "立即更新";
            DownloadProgress.Visibility = Visibility.Collapsed;
            System.Windows.MessageBox.Show(
                this,
                $"下載更新失敗：{ex.Message}",
                "更新失敗",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnDownloadProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = percent;
            DownloadBtnText.Text = percent >= 100 ? "套用中..." : $"下載中 {percent}%";
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 更新進行中禁止關閉視窗（含標題列的 X），避免中斷下載或對已關閉視窗更新進度。
        if (_isUpdating)
            e.Cancel = true;
    }

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ReleaseNotesLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
