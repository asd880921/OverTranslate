using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using OverTranslate.Views.Capture;
using OverTranslate.Views.Settings;

namespace LayoutProbe;

internal static class DebugPanelLayout
{
    internal static void Report(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/OverTranslate;component/Themes/{theme}Theme.xaml")
            };
            var toolbar = new ToolbarWindow(0, 0, 680, 100, "EN", "ZH-HANT");
            try
            {
                var popup = (Popup)toolbar.FindName("DebugPopup");
                var panel = (FrameworkElement)popup.Child;
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                panel.UpdateLayout();
                Save(panel, Path.Combine(outputDirectory, $"debug-panel-{theme}.png"));
                var bar = (FrameworkElement)toolbar.Content;
                bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                bar.Arrange(new Rect(bar.DesiredSize));
                bar.UpdateLayout();
                Save(bar, Path.Combine(outputDirectory, $"toolbar-{theme}.png"));
                Console.WriteLine($"{theme}: panel={panel.ActualWidth}x{panel.ActualHeight}; dismissOutside={!popup.StaysOpen}");
                // Both halves of 框線顯示範圍, because the marker's far end is where it can come out
                // the wrong width and the stored choice shows only one of the two.
                //
                // A fresh page for each, rather than one page with the switch clicked over: the
                // halves are shared-size columns, and re-measuring a tree that was already arranged
                // and is not in a window puts them through a path they never take in the app. Each
                // page reads the stored choice as it is built, so flipping the setting either side
                // of the second one is what a page opened on that choice sees; it is put back before
                // leaving, and the settings file ends on the value it started on.
                var stored = SettingsService.Instance.Current.OcrDebug.ShowOnTranslation;
                try
                {
                    SaveDebugCard(stored, outputDirectory, $"debug-settings-{theme}.png");
                    SettingsService.Instance.UpdateOcrDebug(showOnTranslation: !stored);
                    SaveDebugCard(!stored, outputDirectory, $"debug-settings-{theme}-scope-flipped.png");
                }
                finally { SettingsService.Instance.UpdateOcrDebug(showOnTranslation: stored); }
            }
            finally { toolbar.Close(); }
        }
    }

    /// <summary>
    /// Renders the 偵錯工具 card of a page built just now, with its fold forced open — the fold is
    /// shut on every visit and animated open, and neither state is what this is here to look at.
    /// </summary>
    private static void SaveDebugCard(bool showOnTranslation, string outputDirectory, string fileName)
    {
        var settings = new SettingsPage();
        var fold = (FrameworkElement)settings.FindName("DebugToolsFold");
        fold.Visibility = Visibility.Visible;
        fold.Height = double.NaN;
        var body = (FrameworkElement)settings.FindName("DebugToolsBody");
        body.Opacity = 1;
        body.RenderTransform = Transform.Identity;
        var card = (FrameworkElement)settings.FindName("DebugToolsCard");
        card.Measure(new Size(720, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, 720, card.DesiredSize.Height));
        card.UpdateLayout();

        // States what the picture is of, so a wrong file name cannot pass for a wrong layout.
        Console.WriteLine($"  {fileName}: showOnTranslation={showOnTranslation}");
        Save(card, Path.Combine(outputDirectory, fileName));
    }

    private static void Save(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
