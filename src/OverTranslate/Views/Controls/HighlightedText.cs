using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OverTranslate.Views.Controls;

/// <summary>
/// Fills a <see cref="TextBlock"/> from a string that marks its own key words, so a hint can put the
/// accent colour on the two or three words that carry it.
/// </summary>
/// <remarks>
/// The hints under the shortcut fields say something the user cannot discover any other way — that
/// the capture key doubles as pause/resume, that the window key retargets while a session runs — and
/// read as one flat grey line, which is the weight the interface gives to text nobody has to read.
/// Marking the nouns is what makes the sentence scannable.
///
/// <para>The alternative was splitting each hint into prefix/keyword/suffix keys and assembling
/// <c>&lt;Run&gt;</c>s in XAML. That works for one keyword and falls apart at two, and it forces
/// every translator to keep three fragments in an order the two languages do not share. Here the
/// sentence stays one string in one key, written the way the language wants it, and the marks travel
/// with it — a translator moves <c>[[…]]</c> to wherever the word landed.</para>
///
/// <para>An attached property rather than a subclass of TextBlock, so an existing hint keeps its
/// style and its place in the layout and gains nothing but marked-up content. It re-runs whenever the
/// value changes, which is what makes it survive a change of interface language: the source is bound
/// with DynamicResource, so switching dictionaries pushes a new string through here.</para>
/// </remarks>
public static class HighlightedText
{
    /// <summary>Opens a stretch that should be drawn in the accent colour.</summary>
    /// <remarks>
    /// Doubled brackets because a single one appears in real interface text — key names, "[Esc]" —
    /// and a marker has to be something no translator would type by accident.
    /// </remarks>
    public const string Open = "[[";

    /// <inheritdoc cref="Open"/>
    public const string Close = "]]";

    /// <summary>The brush key the marked stretches are painted with.</summary>
    /// <remarks>
    /// Taken as a resource reference rather than a colour, so the emphasis follows a change of theme
    /// the way every other accented thing on the page does.
    /// </remarks>
    public const string HighlightBrushKey = "AppAccent";

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(string),
            typeof(HighlightedText),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, string? value) =>
        element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element) =>
        (string?)element.GetValue(SourceProperty);

    /// <summary>One stretch of a hint, and whether it is one of the marked ones.</summary>
    public readonly record struct Segment(string Text, bool Highlighted);

    /// <summary>
    /// Splits a marked-up string into its stretches. Kept apart from the rendering so the parsing —
    /// the half with the edge cases — can be tested without a WPF element to hang it on.
    /// </summary>
    /// <remarks>
    /// Anything malformed degrades to plain text rather than throwing or swallowing the sentence. A
    /// hint is the least important text on the page and a missing one is invisible, so the failure
    /// this guards against is a stray bracket costing the user the whole line.
    /// </remarks>
    public static IReadOnlyList<Segment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var segments = new List<Segment>();
        var at = 0;

        while (at < text.Length)
        {
            var open = text.IndexOf(Open, at, StringComparison.Ordinal);
            if (open < 0) break;

            var close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            // An opener with nothing closing it is a typo, not a mark: the rest of the line is text.
            if (close < 0) break;

            if (open > at) segments.Add(new Segment(text[at..open], false));

            var marked = text[(open + Open.Length)..close];
            // "[[]]" marks nothing; dropping it keeps an empty Run out of the TextBlock.
            if (marked.Length > 0) segments.Add(new Segment(marked, true));

            at = close + Close.Length;
        }

        if (at < text.Length) segments.Add(new Segment(text[at..], false));

        return segments;
    }

    /// <remarks>
    /// The visibility is set here, and it has to be. Content supplied as <see cref="Inlines"/> leaves
    /// <see cref="TextBlock.Text"/> at its empty default — and FieldHint, the style every hint on the
    /// settings page wears, collapses a TextBlock whose Text is empty so that a hint with nothing to
    /// say takes up no room. Filling the inlines and leaving that trigger to judge the Text property
    /// hid the hints completely — correct content, invisible line. A local value outranks a style
    /// trigger, so setting it here settles the question for anything using this property and leaves
    /// the trigger to go on serving the hints that do not.
    /// </remarks>
    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();

        var segments = Split(e.NewValue as string);
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Highlighted)
                run.SetResourceReference(TextElement.ForegroundProperty, HighlightBrushKey);
            block.Inlines.Add(run);
        }

        block.Visibility = segments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
