using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Batch;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace OverTranslate.Views.Batch;

/// <summary>One row in the queue. Holds its own thumbnail so the list reads as pictures, not paths.</summary>
public sealed class BatchListItem(string path)
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);
    public string FolderName { get; } = new DirectoryInfo(
        System.IO.Path.GetDirectoryName(path) ?? string.Empty).Name;

    /// <summary>
    /// The areas the user marked on this page, in image pixels. Kept on the row rather than thrown
    /// away with the run, so a second pass — different engine, different target language, or just
    /// a retry — does not mean drawing every box again. Removing the row is what discards them.
    /// </summary>
    public List<Rect> Regions { get; } = [];

    public string RegionSummary => Regions.Count == 0 ? "" : $"已框 {Regions.Count} 區";

    /// <summary>Decoded small: a queue of 60 full-size comic pages would otherwise cost hundreds of MB.</summary>
    public BitmapImage? Thumbnail { get; } = LoadThumbnail(path);

    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 68;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // An unreadable file still belongs in the list — the run reports why it failed.
            return null;
        }
    }
}

public partial class BatchPage : UserControl
{
    private const string ImageFilter =
        "圖片檔|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有檔案|*.*";

    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>
    /// 圖片\OverTranslate\圖片翻譯 — a folder of this feature's own, deliberately fixed rather than
    /// following 設定's screenshot location: translated pages and screenshots are different kinds of
    /// output, and a shared folder turns into a pile. Choosing another folder here is honoured
    /// exactly as given and is not written back to settings, so it cannot disturb screenshots.
    /// </summary>
    private static string DefaultOutputDirectory => System.IO.Path.Combine(
        ScreenshotSaveService.DefaultDirectory, "圖片翻譯");

    private readonly ObservableCollection<BatchListItem> _items = [];
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _outputDirectory = "";

    public BatchPage()
    {
        InitializeComponent();

        ImageList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => RefreshIdleState();

        var settings = SettingsService.Instance.Current;

        SourceLangBox.ItemsSource = LanguageData.OcrSourceLanguages;
        SourceLangBox.SelectedValue = LanguageData.GetValidOcrSourceCode(settings.SourceLanguage);
        if (SourceLangBox.SelectedValue == null) SourceLangBox.SelectedIndex = 0;

        TargetLangBox.ItemsSource = LanguageData.TargetLanguages;
        TargetLangBox.SelectedValue = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        if (TargetLangBox.SelectedValue == null) TargetLangBox.SelectedIndex = 0;

        ProviderBox.ItemsSource = LanguageData.Providers;
        ProviderBox.SelectedValue = settings.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        RefreshProviderHint();

        _outputDirectory = DefaultOutputDirectory;
        OutputBox.Text = _outputDirectory;

        RefreshIdleState();
    }

    /// <summary>
    /// Copies what the user just drew back onto the rows. The picker walks the queue in list order,
    /// so the two line up by index.
    /// </summary>
    private void StoreRegions(IReadOnlyList<BatchImage> queue)
    {
        for (int i = 0; i < queue.Count && i < _items.Count; i++)
        {
            _items[i].Regions.Clear();
            _items[i].Regions.AddRange(queue[i].Regions);
        }

        // BatchListItem is deliberately immutable apart from this, so the rows are told to re-read
        // rather than carrying change notification for one field.
        ImageList.Items.Refresh();
    }

    /// <summary>
    /// The engine is a single app-wide choice, as it is on the translation page and the capture
    /// toolbar, so picking one here is remembered everywhere rather than being a hidden per-page
    /// setting the user has to keep in their head.
    /// </summary>
    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;

        SettingsService.Instance.Current.Provider = provider;
        SettingsService.Instance.Save();
        RefreshProviderHint();
    }

    private void RefreshProviderHint()
    {
        var provider = ProviderBox.SelectedItem as ProviderItem;

        // A missing key is the one thing that would fail every single image in the run, so say so
        // before the user starts rather than in the failure list afterwards.
        if (provider?.RequiresApiKey == true && string.IsNullOrWhiteSpace(SettingsService.Instance.Current.ApiKey))
        {
            ProviderHint.Text = "此服務需要 API 金鑰，請先至「設定」填寫，否則所有圖片都會失敗。";
            ProviderHint.Visibility = Visibility.Visible;
            return;
        }

        ProviderHint.Text = provider?.Hint ?? "";
        ProviderHint.Visibility = string.IsNullOrEmpty(ProviderHint.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Closing the shell takes away the only Stop button, so a run in flight is cancelled with it
    /// rather than left grinding through the queue with nowhere to report to.
    /// </summary>
    public void Teardown() => _cts?.Cancel();

    // ── Building the queue ───────────────────────────────────────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = ImageFilter,
            Title = "選擇要翻譯的圖片",
        };

        if (dialog.ShowDialog() == true)
            AddPaths(dialog.FileNames);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    /// <summary>
    /// Appends rather than replaces — the whole point of the queue is collecting pages from several
    /// folders in turn. Dropped folders are expanded so dragging a chapter in works.
    /// </summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_isRunning) return;

        var existing = _items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        int duplicates = 0;

        foreach (var path in Expand(paths).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.Add(path)) { duplicates++; continue; }
            _items.Add(new BatchListItem(path));
            added++;
        }

        StatusText.Text = (added, duplicates) switch
        {
            ( > 0, 0) => $"已加入 {added} 張圖片",
            ( > 0, _) => $"已加入 {added} 張圖片，{duplicates} 張重複圖片已略過",
            (0, > 0) => $"{duplicates} 張圖片已存在清單中，未新增圖片",
            _ => "沒有可加入的圖片，請確認檔案格式",
        };
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in SafeEnumerate(path))
                    yield return file;
            }
            else if (File.Exists(path) && IsSupported(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Where(IsSupported);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (sender is not FrameworkElement { DataContext: BatchListItem item }) return;

        _items.Remove(item);
        StatusText.Text = $"已移除 {item.FileName}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        var cleared = _items.Count;
        _items.Clear();
        StatusText.Text = cleared > 0 ? $"已清空清單，移除 {cleared} 張圖片" : "";
    }

    // ── Output folder ────────────────────────────────────────────────────────────────────────

    private void OutputBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRunning) return;
        e.Handled = true;   // a read-only box would otherwise just take focus and look inert
        Browse_Click(sender, e);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "翻好的圖要存到哪裡",
            InitialDirectory = Directory.Exists(_outputDirectory) ? _outputDirectory : "",
        };

        if (dialog.ShowDialog() == true)
        {
            _outputDirectory = dialog.FolderName;
            OutputBox.Text = _outputDirectory;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            ScreenshotSaveService.OpenFolder(_outputDirectory);
        }
        catch (Exception)
        {
            StatusText.Text = "無法開啟資料夾，請確認路徑是否存在";
        }
    }

    // ── Running ──────────────────────────────────────────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _items.Count == 0) return;

        // Whole-image mode ignores any boxes still on the rows; it does not erase them, so
        // switching back to marked areas finds the earlier work intact.
        var queue = _items
            .Select(item => new BatchImage(
                item.Path, SelectRegionsRadio.IsChecked == true ? item.Regions : []))
            .ToList();

        if (SelectRegionsRadio.IsChecked == true)
        {
            var picker = new RegionSelectWindow(queue) { Owner = Window.GetWindow(this) };
            picker.ShowDialog();

            if (picker.Result is null)
            {
                StatusText.Text = "已取消框選，清單保留";
                return;
            }

            queue = [.. picker.Result];
            StoreRegions(queue);
        }

        var sourceLang = LanguageData.GetValidOcrSourceCode(SourceLangBox.SelectedValue as string);
        var targetLang = LanguageData.GetValidTargetCode(TargetLangBox.SelectedValue as string);
        var apiKey = SettingsService.Instance.Current.ApiKey;

        EnterRunningState(queue.Count);

        _cts = new CancellationTokenSource();
        using var service = new BatchTranslationService(Dispatcher);

        var progress = new Progress<BatchProgress>(report =>
        {
            Progress.Value = report.Completed;
            StatusText.Text = string.IsNullOrEmpty(report.FileName)
                ? "即將完成"
                : $"正在翻譯第 {report.Completed + 1} 張，共 {report.Total} 張 · {report.FileName}";
        });

        BatchResult result;
        try
        {
            result = await service.RunAsync(
                queue, _outputDirectory, sourceLang, targetLang, apiKey,
                VerticalTextCheckBox.IsChecked == true, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new BatchResult(0, [], Cancelled: true, _outputDirectory);
        }
        catch (Exception ex)
        {
            LeaveRunningState();
            StatusText.Text = $"無法開始翻譯：{ex.Message}";
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        LeaveRunningState();
        ReportResult(result);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止，已完成的圖片會保留";
    }

    private void EnterRunningState(int total)
    {
        _isRunning = true;
        Progress.Minimum = 0;
        Progress.Maximum = total;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Visible;
        StartBtn.Visibility = Visibility.Collapsed;
        AddBtn.IsEnabled = false;
        ClearBtn.IsEnabled = false;
        BrowseBtn.IsEnabled = false;
        SourceLangBox.IsEnabled = false;
        TargetLangBox.IsEnabled = false;
        VerticalTextCheckBox.IsEnabled = false;
        ProviderBox.IsEnabled = false;
        WholeImageRadio.IsEnabled = false;
        SelectRegionsRadio.IsEnabled = false;
    }

    private void LeaveRunningState()
    {
        _isRunning = false;
        Progress.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        AddBtn.IsEnabled = true;
        ClearBtn.IsEnabled = true;
        BrowseBtn.IsEnabled = true;
        SourceLangBox.IsEnabled = true;
        TargetLangBox.IsEnabled = true;
        VerticalTextCheckBox.IsEnabled = true;
        ProviderBox.IsEnabled = true;
        WholeImageRadio.IsEnabled = true;
        SelectRegionsRadio.IsEnabled = true;
        RefreshIdleState();
    }

    private void ReportResult(BatchResult result)
    {
        var failed = result.Failures.Count;
        StatusText.Text = (result.Cancelled, result.Succeeded, failed) switch
        {
            (true, 0, _) => "已停止，尚未完成任何圖片",
            (true, var done, _) => $"已停止，已完成的 {done} 張圖片已儲存",
            (false, 0, 0) => "沒有可處理的圖片",
            (false, var done, 0) => $"已完成 {done} 張圖片的翻譯",
            (false, 0, var bad) => $"{bad} 張圖片全部失敗：{FirstReasons(result)}",
            (false, var done, var bad) => $"已完成 {done} 張圖片，{bad} 張已略過：{FirstReasons(result)}",
        };

        // A long run finishes while the user is off doing something else, so this has to arrive
        // through Windows' own notifications rather than a toast inside a window they left.
        if (result is { Cancelled: false, Succeeded: > 0 })
            TrayNotificationService.Show(
                "圖片翻譯完成",
                $"已完成 {result.Succeeded} 張圖片，已儲存至 {result.OutputDirectory}",
                failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    // Names the files that failed rather than just counting them, so the user knows what to re-check.
    private static string FirstReasons(BatchResult result)
    {
        var shown = result.Failures.Take(2).Select(f => $"{f.FileName}（{f.Reason}）");
        var text = string.Join("、", shown);
        return result.Failures.Count > 2 ? $"{text} 等" : text;
    }

    private void RefreshIdleState()
    {
        if (_isRunning) return;

        RefreshProviderHint();
        CountText.Text = _items.Count == 0 ? "" : $"{_items.Count} 張";
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.IsEnabled = _items.Count > 0;
        ClearBtn.IsEnabled = _items.Count > 0;
    }
}
