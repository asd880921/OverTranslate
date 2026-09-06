using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The identities a grouping diagnostic refers to its lines by, and the guarantee that asking for
/// them changes nothing.
/// </summary>
public class GroupingTraceTests
{
    [Fact]
    public void AMergedLineCanBeFollowedBackToTheBoxesItWasBuiltFrom()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏", new Rect(10, 10, 42, 24)),
            new("夜市", new Rect(58, 10, 42, 24)),
            new("攻略", new Rect(106, 10, 42, 24)),
        }.AsDetected();

        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, trace);

        // Three boxes in, one line out, and the line says which three. Nothing on a merged block
        // itself points back at them — it is a new record built from a union of rectangles — so
        // without this the two halves of a report cannot be read against each other.
        Assert.Equal(["b0", "b1", "b2"], Assert.Single(trace.Lines).SourceIds);
        Assert.Equal("L0", trace.Lines[0].Id);
        Assert.Equal(["L0"], Assert.Single(trace.Groups));
    }

    [Fact]
    public void LinesAreNumberedInTheOrderTheNextLineRulesSeeThem()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("BOTTOM", new Rect(10, 200, 120, 24)),
            new("TOP", new Rect(10, 10, 120, 24)),
        }.AsDetected();

        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, trace);

        // Block ids follow the detector's order and line ids follow the page's, which is why they
        // are two namespaces rather than one: b0 is the bottom line and L0 is the top one.
        Assert.Equal("BOTTOM", trace.Blocks[0].Block.Text);
        Assert.Equal("TOP", trace.Lines[0].Block.Text);
        Assert.Equal(["b1"], trace.Lines[0].SourceIds);
    }

    [Fact]
    public void AskingForTheTraceChangesNothingAboutTheGrouping()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Since its initial release, PaddleOCR has", new Rect(147, 24, 1742, 29)),
            new("proven performance in real-world use", new Rect(147, 62, 1771, 31)),
            new("cost.", new Rect(145, 648, 62, 31)),
            new("On June 11, 2026, PaddleOCR released", new Rect(148, 711, 1755, 27)),
        }.AsDetected();

        var untraced = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General);
        var traced = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General, [], new GroupingTrace());

        Assert.Equal(
            untraced.Select(group => group.Text),
            traced.Select(group => group.Text));
        Assert.Equal(
            untraced.Select(group => group.Lines.Count),
            traced.Select(group => group.Lines.Count));
    }
}
