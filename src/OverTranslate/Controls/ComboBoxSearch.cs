using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OverTranslate.Models;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;

namespace OverTranslate.Controls;

/// <summary>
/// Turns a plain <see cref="ComboBox"/> into a searchable one: while the list is open the picker
/// itself becomes a field, and typing into it narrows the list.
/// </summary>
/// <remarks>
/// An attached behaviour rather than a control of its own, because a searchable picker is not a
/// different kind of picker — it is the same one with a filter above the list. A subclass would have
/// had to be adopted everywhere, would have needed its own copy of ModernComboBox's template (and of
/// QuickLookup's variant of it), and every call site's <c>SelectedValuePath</c>, item binding and
/// <c>SelectionChanged</c> wiring would have had to be re-pointed at a new type for a feature that
/// adds one text box.
///
/// The pieces live in the ComboBox template — <c>PART_SearchBox</c>, <c>PART_NoResults</c>,
/// <c>PART_ItemsScroll</c> — which is also where they are hidden when this is off, so a picker that
/// does not opt in draws exactly what it drew before. The field is in the picker rather than in the
/// dropdown so that an IME can compose into it; see the ComboSearchBox style for that story.
///
/// <para><b>Nothing here ever moves the selection.</b> Filtering hides item containers instead of
/// filtering the collection: a collection-view filter that excludes the selected item makes
/// <see cref="System.Windows.Controls.Primitives.Selector"/> clear <c>SelectedItem</c>, so merely
/// typing would change what the picker is set to — and on these pickers a changed selection saves a
/// setting and re-translates. Hiding containers leaves the collection, and therefore the selection,
/// untouched. The built-in type-to-select is switched off for the same reason: it moves the
/// selection on every keystroke. The user picks by clicking, or by arrowing into the list and
/// pressing Enter there; typing alone never picks anything.</para>
///
/// The lists this is used on are the language lists — a few dozen entries, drawn into the
/// non-virtualising <c>StackPanel</c> the template uses as its items host, so every container is
/// realised and there is one to hide.
/// </remarks>
public static class ComboBoxSearch
{
    private const string SearchBoxPart   = "PART_SearchBox";
    private const string NoResultsPart   = "PART_NoResults";
    private const string ItemsScrollPart = "PART_ItemsScroll";

    /// <summary>How many dispatcher passes to keep asking for focus before giving up.</summary>
    /// <remarks>
    /// The field is collapsed until the list opens, and a collapsed element cannot take focus — so
    /// the first attempt is made after the layout pass that reveals it. Usually one is enough.
    /// Occasionally, on a busy frame, it is not, and a single attempt then fails silently and leaves
    /// the user typing into nothing until they notice and click the box. So it repeats until it
    /// takes, and stops the moment the list closes.
    /// </remarks>
    private const int FocusAttempts = 8;

    /// <summary>Matching ignores case, width (ＡＢ vs AB) and accents.</summary>
    /// <remarks>
    /// Invariant rather than the current culture: the text being matched is a fixed list of language
    /// names, and which of them a search finds should not depend on the machine's regional settings.
    /// Width matters because a user typing with a Chinese IME active produces full-width latin
    /// letters without meaning to.
    /// </remarks>
    private static readonly CompareInfo Comparer = CultureInfo.InvariantCulture.CompareInfo;

    private const CompareOptions MatchOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth |
        CompareOptions.IgnoreKanaType;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ComboBoxSearch),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    /// <summary>The ComboBox a search box belongs to, so its own handlers can find it again.</summary>
    /// <remarks>
    /// Set on the TextBox rather than walked to through the tree, so a handler has its ComboBox in
    /// one read whatever the template around it looks like. It doubles as the "already hooked" mark
    /// — the template is applied once, but the dropdown opens many times.
    /// </remarks>
    private static readonly DependencyProperty OwnerProperty =
        DependencyProperty.RegisterAttached(
            "Owner", typeof(ComboBox), typeof(ComboBoxSearch), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;

        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownClosed -= OnDropDownClosed;
        combo.PreviewKeyDown -= OnComboPreviewKeyDown;

        if (!GetIsEnabled(combo)) return;

        combo.IsTextSearchEnabled = false;
        combo.DropDownOpened += OnDropDownOpened;
        combo.DropDownClosed += OnDropDownClosed;
        combo.PreviewKeyDown += OnComboPreviewKeyDown;
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;

        var box = FindPart<TextBox>(combo, SearchBoxPart);
        if (box is null) return;

        if (!ReferenceEquals(box.GetValue(OwnerProperty), combo))
        {
            box.SetValue(OwnerProperty, combo);
            box.TextChanged += OnSearchTextChanged;
            box.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), true);
        }

        // Every open starts from the whole list. A query left over from last time would be a filter
        // the user cannot see the reason for — they opened the list to look at it, not to resume a
        // search they finished a minute ago.
        box.Clear();
        ShowAllItems(combo);

        FocusSearchBoxWhenReady(combo, box, FocusAttempts);
    }

    /// <summary>Puts the caret in the search box as soon as it is there to take it.</summary>
    private static void FocusSearchBoxWhenReady(ComboBox combo, TextBox box, int attemptsLeft)
    {
        if (!combo.IsDropDownOpen) return;
        if (box.IsKeyboardFocused || box.Focus()) return;
        if (attemptsLeft <= 0) return;

        combo.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () => FocusSearchBoxWhenReady(combo, box, attemptsLeft - 1));
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;

        FindPart<TextBox>(combo, SearchBoxPart)?.Clear();
        ShowAllItems(combo);
    }

    private static void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(OwnerProperty) is ComboBox combo)
            ApplyFilter(combo, box.Text);
    }

    /// <remarks>
    /// Bubbling, not tunnelling: the TextBox has already done its own editing by the time this runs,
    /// so marking a key handled here takes it away from the ComboBox above without taking it away
    /// from the box being typed into. That is the point — an open ComboBox claims Space, Enter, Home,
    /// End and the arrows for its list, and every one of those is a key someone typing a search term
    /// expects to keep. Characters are unaffected either way: WPF raises text input on its own route,
    /// which a handled key-down does not cancel.
    ///
    /// Registered with <c>handledEventsToo</c>, which is not optional here: a TextBox marks the
    /// arrow keys handled as part of moving its own caret, and an ordinary <c>KeyDown</c> handler is
    /// skipped for a handled event — so the down-arrow that is meant to walk into the list would
    /// never arrive. The keys it does not claim (Escape, Enter, Space) reach the ComboBox unless
    /// this stops them, which is what the rest of the switch is for.
    /// </remarks>
    private static void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(OwnerProperty) is not ComboBox combo) return;

        switch (e.Key)
        {
            case Key.Escape:
                // First Escape clears the query and leaves the list open; a second one falls through
                // to the ComboBox and closes it. Clearing is the more common intent, and undoing a
                // mistyped search by reopening the list is a worse way to spend the key.
                if (box.Text.Length > 0)
                {
                    box.Clear();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                // Deliberately nothing. There is no highlighted item to commit — the search does not
                // pre-select a best guess — so the only thing Enter could do is pick something the
                // user never pointed at. Down then Enter picks the first match, and shows which one
                // it is picking first.
                e.Handled = true;
                break;

            case Key.Down:
                e.Handled = FocusItem(combo, fromStart: true);
                break;

            case Key.Up:
                e.Handled = FocusItem(combo, fromStart: false);
                break;

            case Key.Space:
            case Key.Home:
            case Key.End:
            case Key.Left:
            case Key.Right:
            case Key.PageUp:
            case Key.PageDown:
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The keys that belong to an entry once the user has arrowed onto one, rather than to the
    /// search box they arrowed out of.
    /// </summary>
    /// <remarks>
    /// Tunnelling on the ComboBox, which is the only place early enough: ComboBox handles these keys
    /// in a class handler, and a class handler runs before any instance handler on the same element.
    /// Its answers are the wrong ones here. Arrowing in an open WPF ComboBox moves the selection
    /// itself, entry by entry, which is precisely the automatic selection this whole feature is here
    /// to avoid; and its Enter commits the entry <i>it</i> considers highlighted, which is not the
    /// one the user is looking at, because the highlight it means is internal state this cannot set
    /// and keyboard focus is what marks the entry instead (see the IsKeyboardFocused trigger in
    /// SharedStyles). So navigation and commit are done here, over the containers that are actually
    /// still visible under the current query.
    /// </remarks>
    private static void OnComboPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var combo = (ComboBox)sender;
        if (!combo.IsDropDownOpen) return;

        // Only once focus has left the search box for an entry; everything before that is the search
        // box's own business, handled where it is typed.
        if (Keyboard.FocusedElement is not ComboBoxItem item) return;
        if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), combo)) return;

        var index = combo.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) return;

        switch (e.Key)
        {
            case Key.Enter:
                combo.SelectedItem = combo.Items[index];

                // Focus back on the picker before the list it is standing in goes away. WPF's own
                // keyboard close does this and closing by hand does not: left alone, focus stays on
                // an entry inside a popup that is no longer shown, which is focus nowhere — the next
                // key press goes to no one, and the picker cannot be reopened from the keyboard.
                combo.Focus();
                combo.IsDropDownOpen = false;
                e.Handled = true;
                break;

            case Key.Down:
                e.Handled = FocusFrom(combo, index + 1, +1);
                break;

            case Key.Up:
                // Off the top of the matches and back into the search box, so a query can be
                // corrected without reaching for the mouse.
                e.Handled = FocusFrom(combo, index - 1, -1) || FocusSearchBox(combo);
                break;
        }
    }

    /// <summary>Moves keyboard focus, and with it the highlight, onto the first or last match.</summary>
    private static bool FocusItem(ComboBox combo, bool fromStart) =>
        fromStart ? FocusFrom(combo, 0, +1) : FocusFrom(combo, combo.Items.Count - 1, -1);

    /// <summary>Focuses the first entry still visible from <paramref name="start"/> onwards.</summary>
    private static bool FocusFrom(ComboBox combo, int start, int step)
    {
        for (var i = start; i >= 0 && i < combo.Items.Count; i += step)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not ComboBoxItem container)
                continue;
            if (container.Visibility != Visibility.Visible || !container.IsEnabled)
                continue;

            container.BringIntoView();
            return container.Focus();
        }

        return false;
    }

    private static bool FocusSearchBox(ComboBox combo)
    {
        var box = FindPart<TextBox>(combo, SearchBoxPart);
        if (box is null) return false;

        box.CaretIndex = box.Text.Length;
        return box.Focus();
    }

    /// <summary>Whether an item would survive <paramref name="query"/> — the rule the box applies.</summary>
    /// <remarks>Public so the matching rules can be tested without standing up a ComboBox.</remarks>
    public static bool Matches(object? item, string query) => IsMatch(item, SplitTerms(query));

    private static string[] SplitTerms(string query) => query.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ApplyFilter(ComboBox combo, string query)
    {
        var terms = SplitTerms(query);

        var matches = 0;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container)
                continue;

            var visible = IsMatch(combo.Items[i], terms);
            container.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) matches++;
        }

        var empty = FindPart<UIElement>(combo, NoResultsPart);
        if (empty is not null)
            empty.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The list under a query is a different list, and a leftover scroll offset would open it
        // part-way down — occasionally past every match there is.
        FindPart<ScrollViewer>(combo, ItemsScrollPart)?.ScrollToTop();
    }

    /// <remarks>
    /// All terms must appear, each of them anywhere in the item's searchable text rather than only at
    /// its start: "chinese" has to find 繁體中文 Traditional Chinese, and it is the second word there.
    /// Several terms are and-ed so a query can narrow from two directions at once — "trad chinese",
    /// "zh hant".
    /// </remarks>
    private static bool IsMatch(object? item, string[] terms)
    {
        if (terms.Length == 0) return true;

        var haystack = SearchTextOf(item);
        if (haystack.Length == 0) return false;

        foreach (var term in terms)
        {
            if (Comparer.IndexOf(haystack, term, MatchOptions) < 0)
                return false;
        }

        return true;
    }

    private static string SearchTextOf(object? item) => item switch
    {
        ISearchableItem searchable => searchable.SearchText,
        null                       => "",
        _                          => item.ToString() ?? "",
    };

    private static void ShowAllItems(ComboBox combo)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is UIElement container)
                container.Visibility = Visibility.Visible;
        }

        var empty = FindPart<UIElement>(combo, NoResultsPart);
        if (empty is not null) empty.Visibility = Visibility.Collapsed;

        FindPart<ScrollViewer>(combo, ItemsScrollPart)?.ScrollToTop();
    }

    private static T? FindPart<T>(ComboBox combo, string name) where T : class
    {
        combo.ApplyTemplate();
        return combo.Template?.FindName(name, combo) as T;
    }
}
