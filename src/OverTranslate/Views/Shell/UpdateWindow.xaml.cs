using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Services;

namespace OverTranslate.Views.Shell;

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
            ErrorText.Visibility = Visibility.Collapsed;
            DownloadBtnText.Text = "下載中 0%";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Visible;

            await UpdateService.DownloadAndApplyAsync(_updateInfo, OnDownloadProgress, OnApplyingAsync);
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            DismissBtn.IsEnabled = true;
            DownloadBtn.IsEnabled = true;
            DownloadBtnText.Text = "重試";
            DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"下載更新失敗：{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnDownloadProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = percent;
            DownloadBtnText.Text = $"下載中 {percent}%";
        });
    }

    // The download is finished here regardless of what the last reported percentage was, so the bar
    // is carried the rest of the way before handing over. Without this the display would jump
    // straight from whatever Velopack last reported (often ~70) to the apply phase, making the
    // remaining percent look like it vanished.
    private async Task OnApplyingAsync()
    {
        await AnimateProgressToFullAsync();

        // The apply step exposes no progress at all, so an indeterminate bar is the honest signal:
        // still working, duration unknown. It keeps moving, which a bar frozen at 100% would not.
        DownloadProgress.IsIndeterminate = true;
        DownloadBtnText.Text = "套用中…";

        // Let those two land on screen: ApplyUpdatesAndRestart blocks this thread and then kills the
        // process, so anything not painted by now is never painted at all.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private Task AnimateProgressToFullAsync()
    {
        var completed = new TaskCompletionSource();

        DownloadBtnText.Text = "下載中 100%";
        var toFull = new DoubleAnimation(100, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        toFull.Completed += (_, _) =>
        {
            // Release the animation's hold on Value so IsIndeterminate can take the bar over.
            DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            DownloadProgress.Value = 100;
            completed.TrySetResult();
        };
        DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, toFull);

        return completed.Task;
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
