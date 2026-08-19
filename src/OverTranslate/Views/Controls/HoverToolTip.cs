using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so this name collides
using ToolTip = System.Windows.Controls.ToolTip;

namespace OverTranslate.Views.Controls;

/// <summary>
/// A hint that follows the pointer and nothing else: it appears once the pointer has rested on the
/// element, stays for as long as it rests there, and goes when it leaves. Clicking does nothing.
/// </summary>
/// <remarks>
/// WPF's own ToolTipService closes a tooltip the instant any mouse button goes down, anywhere on
/// screen. On a help icon that is wrong twice over — the icon does nothing when clicked, so the
/// click is nobody's instruction to dismiss anything, and a hint that disappears the moment someone
/// clicks it to hold it still reads as the interface breaking under them. Nothing the service
/// offers turns that off: ShowDuration only caps how long it may stay, and ToolTip.StaysOpen
/// governs clicks outside a tooltip the service has already decided to close. So the service is
/// left out of it entirely — the element's own ToolTip property is never set, and the popup is
/// opened and closed from here.
///
/// <para>Set <see cref="ContentProperty"/> in place of ToolTip. A string, or a panel for a hint
/// that is more than one sentence. Bound with DynamicResource it survives a change of interface
/// language, the same as any other string in the interface.</para>
/// </remarks>
public static class HoverToolTip
{
    /// <summary>How long the pointer has to rest before the hint appears.</summary>
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Anchored under the element rather than under the pointer, where WPF puts its own.
    /// </summary>
    /// <remarks>
    /// The popup is a window in its own right, so one opening under the pointer takes the pointer
    /// off the element — which closes it, which puts the pointer back on the element, which opens
    /// it again. Anchoring to the element leaves the pointer where it was.
    /// </remarks>
    private const double GapBelowTarget = 4;

    /// <summary>What to show. Replaces ToolTip on the element.</summary>
    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.RegisterAttached(
            "Content", typeof(object), typeof(HoverToolTip),
            new PropertyMetadata(null, OnContentChanged));

    public static object? GetContent(DependencyObject element) => element.GetValue(ContentProperty);

    public static void SetContent(DependencyObject element, object? value) =>
        element.SetValue(ContentProperty, value);

    /// <summary>The hint built for one element, kept so a later change of content reuses it.</summary>
    private static readonly DependencyProperty HintProperty =
        DependencyProperty.RegisterAttached("Hint", typeof(Hint), typeof(HoverToolTip));

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // A change of interface language pushes a new string through here; the hint that is already
        // wired to this element takes it, rather than a second one being built alongside it.
        if (element.GetValue(HintProperty) is Hint hint)
        {
            hint.Content = e.NewValue;
            return;
        }

        element.SetValue(HintProperty, new Hint(element, e.NewValue));
    }

    private sealed class Hint
    {
        private readonly FrameworkElement _target;
        private readonly ToolTip _tip;
        private readonly DispatcherTimer _delay;

        /// <summary>Subscribed to only while the hint is up, so nothing is left hooked to a window.</summary>
        private Window? _window;

        internal Hint(FrameworkElement target, object? content)
        {
            _target = target;
            _tip = new ToolTip
            {
                Content = content,
                // Nothing but this class may open or close it
                StaysOpen = true,
                Placement = PlacementMode.Bottom,
                PlacementTarget = target,
                VerticalOffset = GapBelowTarget
            };

            _delay = new DispatcherTimer { Interval = OpenDelay };
            _delay.Tick += OnDelayElapsed;

            target.MouseEnter += (_, _) => _delay.Start();
            target.MouseLeave += (_, _) => Hide();

            // The popup is a window of its own and does not go anywhere when the panel holding the
            // icon is dismissed, so it has to be taken down with it.
            target.IsVisibleChanged += (_, _) => { if (!target.IsVisible) Hide(); };
            target.Unloaded += (_, _) => Hide();
        }

        internal object? Content { set => _tip.Content = value; }

        private void OnDelayElapsed(object? sender, EventArgs e)
        {
            _delay.Stop();

            // The pointer may have moved on during the wait
            if (!_target.IsMouseOver) return;

            _window = Window.GetWindow(_target);
            if (_window is not null) _window.Deactivated += OnWindowDeactivated;
            _tip.IsOpen = true;
        }

        /// <summary>
        /// Switching to another application leaves no pointer to leave, so there would be nothing
        /// else to take the hint down and it would sit on top of whatever was switched to.
        /// </summary>
        private void OnWindowDeactivated(object? sender, EventArgs e) => Hide();

        private void Hide()
        {
            _delay.Stop();

            if (_window is not null)
            {
                _window.Deactivated -= OnWindowDeactivated;
                _window = null;
            }

            _tip.IsOpen = false;
        }
    }
}
