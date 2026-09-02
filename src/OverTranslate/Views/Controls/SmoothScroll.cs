using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OverTranslate.Views.Controls;

/// <summary>
/// Scrolls a <see cref="ScrollViewer"/> to an offset over time instead of jumping to it.
/// </summary>
/// <remarks>
/// <para><see cref="ScrollViewer.VerticalOffset"/> is read-only, so it cannot be animated and
/// <see cref="ScrollViewer.ScrollToVerticalOffset"/> arrives instantly. This attaches an animatable
/// property that forwards each value on, which is the standard way round it.</para>
///
/// <para>The offset asked for does not have to be reachable yet. ScrollToVerticalOffset clamps to
/// whatever the content allows at that moment, so an animation aimed past the current bottom simply
/// follows the content as it grows — which is exactly the case this exists for: revealing a panel
/// that is still opening.</para>
/// </remarks>
internal static class SmoothScroll
{
    private static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    private static void OnVerticalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ScrollViewer viewer) viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    /// <summary>
    /// Eases <paramref name="viewer"/> to <paramref name="offset"/>, or jumps there when the user
    /// has animations off.
    /// </summary>
    public static void To(ScrollViewer viewer, double offset, Duration duration, IEasingFunction easing)
    {
        viewer.BeginAnimation(VerticalOffsetProperty, null);

        if (!SystemParameters.ClientAreaAnimation)
        {
            viewer.ScrollToVerticalOffset(offset);
            return;
        }

        // Seeded with where the viewer actually is, so an animation started mid-scroll carries on
        // from there rather than from wherever the last one was aimed.
        viewer.SetValue(VerticalOffsetProperty, viewer.VerticalOffset);
        viewer.BeginAnimation(
            VerticalOffsetProperty, new DoubleAnimation(offset, duration) { EasingFunction = easing });
    }
}
