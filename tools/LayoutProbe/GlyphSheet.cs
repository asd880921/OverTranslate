using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LayoutProbe;

/// <summary>
/// Draws a sheet of candidate icon glyphs so the ones that exist can be told from the ones that do
/// not.
/// </summary>
/// <remarks>
/// A missing codepoint in Segoe Fluent Icons renders as a hollow box rather than failing, so a glyph
/// picked from memory and shipped is a bug nobody sees until a user opens that panel. This is how the
/// set gets chosen: draw the candidates, look, keep what rendered.
/// </remarks>
internal static class GlyphSheet
{
    private static readonly string[] Candidates =
    [
        // cube / package / model
        "E7B8", "EA86", "E8F1", "E96A", "F158", "E7F4", "E81E", "EB41",
        // thermometer / gauge / speed
        "E9CA", "EC4A", "E9D9", "F1AD", "E945", "EC49", "E7EF", "F156",
        // sliders / tuning
        "E9E9", "E713", "E15E", "E71C", "EF58", "E8B3", "F8A5", "E90F",
        // hash / number / seed
        "E8EC", "E943", "E8EF", "F272", "E8A5", "E7C1", "E8FD", "E9F5",
    ];

    public static void Save(string outputDirectory)
    {
        var panel = new WrapPanel
        {
            Width = 760,
            Background = Brushes.White,
        };

        foreach (var code in Candidates)
        {
            var cell = new StackPanel { Width = 92, Margin = new Thickness(0, 10, 0, 10) };
            cell.Children.Add(new TextBlock
            {
                Text = char.ConvertFromUtf32(int.Parse(code, NumberStyles.HexNumber)),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            cell.Children.Add(new TextBlock
            {
                Text = code,
                FontSize = 11,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(cell);
        }

        var path = Path.Combine(outputDirectory, "glyph-sheet.png");
        PromptPanelLayout.Render(panel, 760, double.PositiveInfinity, path);
        Console.WriteLine($"  glyph-sheet.png: {Candidates.Length} candidates");
    }
}
