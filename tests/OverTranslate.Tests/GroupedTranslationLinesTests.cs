using System.Collections.Generic;
using System.Linq;
using System.Windows;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class GroupedTranslationLinesTests
{
    private static TranslatedBlock Group(string translated, params Rect[] lines) =>
        new(
            "source text",
            translated,
            lines.Aggregate(Rect.Union),
            lines,
            SourceGlyphHeight: 16);

    /// <summary>
    /// The whole point: the sentence is translated as one and drawn as three, on the boxes it was
    /// read from. Reading the pieces in order gives the translation back unchanged.
    /// </summary>
    [Fact]
    public void PutsAGroupsTranslationBackOnItsOwnLines()
    {
        var group = Group(
            "我從沒想過你真的會在這一切之後回到這裡",
            new Rect(40, 40, 300, 34),
            new Rect(40, 82, 330, 34),
            new Rect(40, 124, 290, 34));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.Equal(3, placed.Count);
        Assert.Equal(new Rect(40, 40, 300, 34), placed[0].Bounds);
        Assert.Equal(new Rect(40, 82, 330, 34), placed[1].Bounds);
        Assert.Equal(new Rect(40, 124, 290, 34), placed[2].Bounds);
        Assert.Equal(
            "我從沒想過你真的會在這一切之後回到這裡",
            string.Concat(placed.Select(block => block.TranslatedText)));
    }

    /// <summary>
    /// Each line is a line again, so the overlay lays it out as one instead of as a paragraph-sized
    /// bubble over the union of the three.
    /// </summary>
    [Fact]
    public void APlacedLineIsNoLongerAGroup()
    {
        var group = Group(
            "第一段文字第二段文字",
            new Rect(10, 10, 200, 30),
            new Rect(10, 44, 200, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.All(placed, block => Assert.Null(block.SourceLineBounds));
    }

    /// <summary>
    /// Shares are taken by box width, because width is what the text has to fit into. A line twice
    /// as wide as its neighbour takes about twice the translation.
    /// </summary>
    [Fact]
    public void GivesAWiderLineMoreOfTheTranslation()
    {
        var group = Group(
            "一二三四五六七八九十十一十二",
            new Rect(10, 10, 400, 30),
            new Rect(10, 44, 200, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.Equal(2, placed.Count);
        Assert.True(
            placed[0].TranslatedText.Length > placed[1].TranslatedText.Length,
            $"expected the wider line to take more: got {placed[0].TranslatedText.Length} " +
            $"and {placed[1].TranslatedText.Length}");
    }

    /// <summary>
    /// A target language that spaces its words is cut at the spaces — a line ending mid-word reads
    /// as a misrecognition rather than as a wrap.
    /// </summary>
    [Fact]
    public void CutsALatinTranslationAtWordBoundaries()
    {
        var group = Group(
            "I never thought you would actually come back here",
            new Rect(10, 10, 200, 30),
            new Rect(10, 44, 200, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.All(placed, block => Assert.DoesNotContain("  ", block.TranslatedText));
        Assert.Equal(
            "I never thought you would actually come back here",
            string.Join(" ", placed.Select(block => block.TranslatedText)));
    }

    /// <summary>
    /// Closing punctuation belongs to the line it closes. Starting a line with 「。」 is the one
    /// thing a cut in unspaced text can visibly get wrong.
    /// </summary>
    [Fact]
    public void DoesNotStrandClosingPunctuationAtTheStartOfALine()
    {
        var group = Group(
            "他回來了。我沒想到。",
            new Rect(10, 10, 200, 30),
            new Rect(10, 44, 200, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.All(placed, block => Assert.False(
            block.TranslatedText.StartsWith('。'),
            $"a line began with a full stop: \"{block.TranslatedText}\""));
    }

    /// <summary>
    /// A block that was never a group is passed through untouched — the ordinary single-line case,
    /// which is most of every capture.
    /// </summary>
    [Fact]
    public void LeavesAnUngroupedBlockAlone()
    {
        var single = new TranslatedBlock("Exit", "離開", new Rect(10, 10, 70, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([single]);

        Assert.Same(single, Assert.Single(placed));
    }

    /// <summary>
    /// A group whose translation came back empty still yields one block per line, so the bubbles
    /// keep covering the source text instead of leaving the original showing through.
    /// </summary>
    [Fact]
    public void StillCoversEveryLineWhenThereIsNothingToPlace()
    {
        var group = Group(
            "   ",
            new Rect(10, 10, 200, 30),
            new Rect(10, 44, 200, 30));

        var placed = GroupedTranslationLines.SplitOntoSourceLines([group]);

        Assert.Equal(2, placed.Count);
        Assert.All(placed, block => Assert.Equal(string.Empty, block.TranslatedText));
    }
}
