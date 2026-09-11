using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Views.Settings;

namespace LayoutProbe;

/// <summary>
/// Renders the two screens that show a prompt — the library card's preview pane and the editor
/// card — offscreen, in both themes.
/// </summary>
/// <remarks>
/// Both live inside <see cref="ServiceSettingsOverlay"/>, which cannot be loaded without an
/// <see cref="Application"/> for the same reason the capture windows cannot: StaticResource is
/// resolved at parse time. So the only way to look at them without clicking through the app is from
/// here, and the pair is worth looking at together — the System / User panels are meant to read as
/// the same object in both.
///
/// Nothing is saved and no setting is written: the overlay is opened, drawn, and dropped.
/// </remarks>
internal static class PromptPanelLayout
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

            SaveLibrary(theme, outputDirectory);
            SaveEditor(theme, outputDirectory);
        }
    }

    /// <summary>The list and the preview beside it, as the settings page draws them.</summary>
    private static void SaveLibrary(string theme, string outputDirectory)
    {
        var overlay = new ServiceSettingsOverlay();
        overlay.Open(TranslationProvider.OpenAI);

        // The whole panel too: this change took two fields off the top of it, and an emptied Grid
        // row that still reserves space is the kind of thing only a picture shows.
        var panel = (FrameworkElement)overlay.FindName("OpenAiPanel");
        Render(panel, 760, double.PositiveInfinity, Path.Combine(outputDirectory, $"openai-panel-{theme}.png"));
        Console.WriteLine($"  openai-panel-{theme}.png: {panel.ActualWidth:F0}x{panel.ActualHeight:F0}");

        var card = (FrameworkElement)overlay.FindName("PromptLibraryCard");

        // A height as well as a width: the preview's two halves share the space they are given, so
        // arranged to their own desired height they would both collapse to one line of text and the
        // split would not be visible at all.
        Render(card, 760, 470, Path.Combine(outputDirectory, $"prompt-library-{theme}.png"));
        Console.WriteLine($"  prompt-library-{theme}.png: {card.ActualWidth:F0}x{card.ActualHeight:F0}");

        // Again with the pane squeezed. The row of parameter cards is three equal columns, and equal
        // columns only stay equal while what is in them can be made narrower — a label that refuses
        // to trim pushes its own card wide and the row past the edge of the pane it sits in, which is
        // what it did the first time this was built.
        Render(card, 560, 470, Path.Combine(outputDirectory, $"prompt-library-narrow-{theme}.png"));
        Console.WriteLine($"  prompt-library-narrow-{theme}.png: {card.ActualWidth:F0}x{card.ActualHeight:F0}");
    }

    /// <summary>The editor, opened on a new prompt, which is how it opens with the built-in wording in.</summary>
    private static void SaveEditor(string theme, string outputDirectory)
    {
        var editor = new PromptEditOverlay();
        editor.Open(automatic: true, profile: null, suggestedName: "我的設定 1");

        var card = (FrameworkElement)editor.FindName("Card");
        Render(card, 720, double.PositiveInfinity, Path.Combine(outputDirectory, $"prompt-editor-{theme}.png"));
        Console.WriteLine($"  prompt-editor-{theme}.png: {card.ActualWidth:F0}x{card.ActualHeight:F0}");

        // Scrolled to the bottom as well: the User Prompt row is below the card's fold, so a shot of
        // the top says nothing about it - and the two rows are the same markup, which is exactly the
        // claim a picture should be made to support rather than stand in for.
        var scroller = Descendants(card).OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is not null)
        {
            scroller.ScrollToBottom();
            scroller.UpdateLayout();
            Render(card, 720, double.PositiveInfinity, Path.Combine(outputDirectory, $"prompt-editor-foot-{theme}.png"));
            Console.WriteLine($"  prompt-editor-foot-{theme}.png written");
            scroller.ScrollToTop();
            scroller.UpdateLayout();
        }

        // Again with both halves empty. Neither placeholder is visible in the shot above, and the
        // pair only makes sense read together - each says the other one would also do.
        ((TextBox)editor.FindName("AutoSystemBox")).Text = "";
        ((TextBox)editor.FindName("ExplicitSystemBox")).Text = "";
        Render(card, 720, double.PositiveInfinity, Path.Combine(outputDirectory, $"prompt-editor-empty-{theme}.png"));
        Console.WriteLine($"  prompt-editor-empty-{theme}.png: {card.ActualWidth:F0}x{card.ActualHeight:F0}");

        // The 進階 fold, opened by hand rather than by its button: the button animates the host from a
        // height of zero, and an animation offscreen has no clock to finish on. Three rows of
        // controls that are never photographed are three rows nobody has looked at.
        var host = (FrameworkElement)editor.FindName("AdvancedHost");
        host.Height = double.NaN;
        host.Opacity = 1;
        host.IsEnabled = true;

        Render(card, 720, double.PositiveInfinity, Path.Combine(outputDirectory, $"prompt-editor-advanced-{theme}.png"));
        Console.WriteLine($"  prompt-editor-advanced-{theme}.png: {card.ActualWidth:F0}x{card.ActualHeight:F0}");
    }

    internal static void Render(FrameworkElement visual, double width, double height, string path)
    {
        // Arranged at the size the element asked for rather than at the size it was offered: these
        // cards centre themselves and cap their own width, so arranging them into the offered
        // rectangle draws the card at its real width with its contents laid out for a wider one.
        visual.Measure(new Size(width, height));
        var arrangedWidth = double.IsInfinity(width)
            ? visual.DesiredSize.Width
            : Math.Min(width, visual.DesiredSize.Width);
        var arrangedHeight = double.IsInfinity(height) ? visual.DesiredSize.Height : height;
        visual.Arrange(new Rect(0, 0, arrangedWidth, arrangedHeight));
        visual.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
