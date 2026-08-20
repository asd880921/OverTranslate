using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What 取詞翻譯 does with the text it copies out of somebody else's window.
/// </summary>
/// <remarks>
/// The copy itself is not testable here — it synthesises keystrokes into whatever has the
/// foreground, which in a test run is the runner. What is pinned is the half that decides what the
/// user ends up looking at in the box.
/// </remarks>
public class SelectedTextReaderTests
{
    [Fact]
    public void A_selection_dragged_across_wrapped_lines_arrives_as_one_sentence()
    {
        // Line breaks in a selection belong to the page's layout, not to the sentence, and reach the
        // translation engine as sentence boundaries that are not there.
        var text = SelectedTextReader.Sanitize("the quick\r\nbrown   fox\njumps");

        Assert.Equal("the quick brown fox jumps", text);
    }

    [Fact]
    public void Nothing_selected_reads_as_nothing_rather_than_as_whitespace()
    {
        // An empty box is what tells the popup to wait for typing; a box holding a space would look
        // empty and translate.
        Assert.Equal("", SelectedTextReader.Sanitize("   \r\n  "));
        Assert.Equal("", SelectedTextReader.Sanitize(null));
    }

    [Fact]
    public void A_selection_that_got_away_is_capped_rather_than_carried_whole()
    {
        var text = SelectedTextReader.Sanitize(new string('a', SelectedTextReader.MaxLength + 500));

        Assert.Equal(SelectedTextReader.MaxLength, text.Length);
    }

    [Fact]
    public void The_cap_never_leaves_a_trailing_space_behind()
    {
        // Cutting mid-gap would otherwise hand the engine a sentence ending in a space, and the box
        // a caret sitting one character past the last word.
        var raw = new string('a', SelectedTextReader.MaxLength - 1) + "   tail";

        Assert.Equal(new string('a', SelectedTextReader.MaxLength - 1), SelectedTextReader.Sanitize(raw));
    }
}
