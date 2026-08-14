using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Controls;

/// <summary>One segment of a <see cref="SegmentedControl"/>.</summary>
public sealed class SegmentedItem : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isMarked;
    private bool _isSelected;

    public SegmentedItem(string text) => _text = text;

    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>
    /// Whether this segment shows its "changed from the default" dot.
    /// </summary>
    /// <remarks>
    /// The point of the marker is the segment that is *not* on screen: a control that hides one of
    /// its two panes has to say something about the hidden one, or an edit made there is invisible
    /// until someone happens to switch back.
    /// </remarks>
    public bool IsMarked { get => _isMarked; set => Set(ref _isMarked, value); }

    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A capsule of mutually exclusive segments, with the selected one marked by a filled pill that
/// slides between them.
/// </summary>
/// <remarks>
/// The indicator moves rather than each segment switching its own background on and off, because a
/// single travelling shape is what makes the two segments read as two positions of one control.
/// Its motion is a plain ease-out with no overshoot: bounce belongs to gestures that carried
/// momentum, and a click on a tab carried none.
/// </remarks>
public partial class SegmentedControl : UserControl
{
    // Apple's move/reposition spring is damping 1.0, response 0.4 — critically damped, no
    // overshoot. An ease-out of about this length is the closest a plain WPF animation gets.
    private static readonly Duration SlideDuration =
        new(TimeSpan.FromMilliseconds(250));

    private bool _measured;

    public SegmentedControl()
    {
        InitializeComponent();
        SegmentHost.ItemsSource = Items;
        Items.CollectionChanged += (_, _) => ApplySelection(animate: false);
        Track.SizeChanged += (_, _) => LayoutIndicator(animate: false);
    }

    /// <summary>The segments, in the order they appear.</summary>
    public ObservableCollection<SegmentedItem> Items { get; } = [];

    /// <summary>Raised when the user picks a different segment. Setting the index does not raise it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Which segment is selected, or -1 when there are none.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Selects a segment without raising <see cref="SelectionChanged"/>.</summary>
    public void Select(int index, bool animate = true)
    {
        if (index < 0 || index >= Items.Count || index == SelectedIndex) return;
        SelectedIndex = index;
        ApplySelection(animate);
    }

    private void Segment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SegmentedItem item }) return;

        var index = Items.IndexOf(item);
        if (index < 0 || index == SelectedIndex) return;

        SelectedIndex = index;
        ApplySelection(animate: true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySelection(bool animate)
    {
        if (SelectedIndex >= Items.Count) SelectedIndex = Items.Count - 1;
        if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;

        for (var i = 0; i < Items.Count; i++)
            Items[i].IsSelected = i == SelectedIndex;

        LayoutIndicator(animate);
    }

    private void LayoutIndicator(bool animate)
    {
        if (Items.Count == 0 || SelectedIndex < 0)
        {
            Indicator.Width = 0;
            return;
        }

        var available = Track.ActualWidth - Track.Padding.Left - Track.Padding.Right
                        - Track.BorderThickness.Left - Track.BorderThickness.Right;
        if (available <= 0) return;

        var segment = available / Items.Count;
        Indicator.Width = segment;
        Indicator.Height = Track.ActualHeight - Track.Padding.Top - Track.Padding.Bottom
                           - Track.BorderThickness.Top - Track.BorderThickness.Bottom;

        var target = segment * SelectedIndex;

        // The very first layout pass places the indicator; animating it would show it flying in
        // from the left edge on a page the user has only just opened. Windows' own "show
        // animations" setting turns the rest off — reduced motion means a gentler equivalent, and
        // for a jump this small that is simply arriving.
        if (!animate || !_measured || !SystemParameters.ClientAreaAnimation)
        {
            IndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            IndicatorShift.X = target;
            _measured = true;
            return;
        }

        IndicatorShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, SlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
