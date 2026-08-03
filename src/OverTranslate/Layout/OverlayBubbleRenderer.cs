using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Layout;

/// <summary>
/// Turns computed <see cref="OverlayBubble"/>s into WPF elements. Background and text go on
/// separate canvases so the whole translation layer can be hidden or composited as one, and so no
/// bubble's opaque background can paint over a neighbour's text.
/// </summary>
public static class OverlayBubbleRenderer
{
    public static void Populate(
        IReadOnlyList<OverlayBubble> bubbles, Canvas backgroundCanvas, Canvas textCanvas)
    {
        foreach (var bubble in bubbles)
        {
            var background = new Border
            {
                Background = new SolidColorBrush(bubble.Background),
                Padding = new Thickness(3, 2, 3, 2),
                Width = bubble.Width,
                Height = bubble.Height,
                ClipToBounds = true,
            };

            var text = new Border
            {
                Padding = new Thickness(3, 2, 3, 2),
                Width = bubble.Width,
                Height = bubble.Height,
                ClipToBounds = true,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = bubble.Text,
                    FontSize = bubble.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(bubble.Foreground),
                    TextWrapping = bubble.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = bubble.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily(OverlayBubbleLayout.FontFamilyName),
                }
            };

            Canvas.SetLeft(background, bubble.Left);
            Canvas.SetTop(background, bubble.Top);
            Canvas.SetLeft(text, bubble.Left);
            Canvas.SetTop(text, bubble.Top);

            backgroundCanvas.Children.Add(background);
            textCanvas.Children.Add(text);
        }
    }
}
