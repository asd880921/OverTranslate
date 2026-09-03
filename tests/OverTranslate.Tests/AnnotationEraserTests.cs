using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the 標記 eraser actually takes away. The claim being checked is that it is a circle: the ring
/// drawn under the pointer is a promise about what will disappear, and these say the promise is kept.
/// </summary>
/// <remarks>
/// Asked of the stroke's mask rather than of a shape cut out of it — see <see cref="EraseMask"/>. The
/// questions are the same ones as before ("is there still ink here?"); only where the answer is kept
/// has changed. A scale of 1 keeps a mask pixel and a DIP the same size, so the numbers in these
/// tests are the coordinates the user would be pointing at.
/// </remarks>
public class AnnotationEraserTests
{
    private static AnnotationStroke Stroke(
        double thickness, AnnotationTool tool, params (double X, double Y)[] points) =>
        new()
        {
            Tool      = tool,
            Color     = Colors.Orange,
            Thickness = thickness,
            Opacity   = 1,
            Points    = [.. points.Select(p => new Point(p.X, p.Y))],
        };

    private static AnnotationStroke Rub(AnnotationStroke stroke, double x, double y, double radius) =>
        Rub(stroke, x, y, x, y, radius);

    private static AnnotationStroke Rub(
        AnnotationStroke stroke, double x0, double y0, double x1, double y1, double radius)
    {
        var rubbed = stroke.WithOwnMask(scale: 1);
        rubbed.Mask!.Erase(new Point(x0, y0), new Point(x1, y1), radius);
        return rubbed;
    }

    /// <summary>Whether ink is still showing there. The rim is a ramp, so the two are not asked at once.</summary>
    private static bool InkAt(AnnotationStroke stroke, double x, double y) =>
        stroke.Mask!.CoverageAt(new Point(x, y)) > 0.5;

    [Fact]
    public void ARubInTheMiddle_TakesTheMiddleAndLeavesTheEnds()
    {
        var rubbed = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)), 50, 0, 10);

        Assert.False(InkAt(rubbed, 50, 0));
        Assert.True(InkAt(rubbed, 5, 0));
        Assert.True(InkAt(rubbed, 95, 0));
    }

    /// <summary>
    /// The test the whole design turns on. A small eraser run down the middle of a wide highlight has
    /// to take a small bite out of it — not the entire band, which is what cutting the centre line
    /// would do, and not nothing.
    /// </summary>
    [Fact]
    public void ASmallRubOnAWideStroke_TakesOnlyWhatTheCircleCovered()
    {
        var rubbed = Rub(Stroke(30, AnnotationTool.Highlighter, (0, 0), (100, 0)), 50, 0, 6);

        // Gone where the circle was...
        Assert.False(InkAt(rubbed, 50, 0));

        // ...and still there directly above and below it, inside the same 30px band.
        Assert.True(InkAt(rubbed, 50, 12));
        Assert.True(InkAt(rubbed, 50, -12));
    }

    [Fact]
    public void ARubThatCoversEverything_LeavesNothingShowing()
    {
        var rubbed = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (10, 0)), 5, 0, 60);

        foreach (double x in new[] { 0.0, 5.0, 10.0 })
            Assert.False(InkAt(rubbed, x, 0));
    }

    [Fact]
    public void ARubNowhereNearIt_LeavesTheStrokeWhole()
    {
        var stroke = Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0));
        var rubbed = Rub(stroke, 50, 400, 10);

        foreach (double x in new[] { 1.0, 25.0, 50.0, 75.0, 99.0 })
            Assert.True(InkAt(rubbed, x, 0), $"ink missing at x={x}");
    }

    /// <summary>
    /// A drag is sampled, so what one step takes has to be the circle's whole path and not a circle
    /// at each end with a gap between them. Two points 200 apart, and the middle must go too.
    /// </summary>
    [Fact]
    public void ADragBetweenTwoDistantPoints_SweepsTheWholeWay()
    {
        var rubbed = Rub(Stroke(60, AnnotationTool.Highlighter, (0, 0), (200, 0)), 0, 0, 200, 0, 10);

        Assert.False(InkAt(rubbed, 100, 0));
        Assert.False(InkAt(rubbed, 100, 8));

        // And no wider than the circle: the band is 60 across and only the middle 20 was rubbed.
        Assert.True(InkAt(rubbed, 100, 20));
    }

    /// <summary>Rubbing twice takes both bites, not just the second.</summary>
    [Fact]
    public void TwoRubs_BothStick()
    {
        var once  = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)), 25, 0, 8);
        once.Mask!.Erase(new Point(75, 0), new Point(75, 0), 8);

        Assert.False(InkAt(once, 25, 0));
        Assert.False(InkAt(once, 75, 0));
        Assert.True(InkAt(once, 50, 0));
    }

    /// <summary>Nothing to rub out reports as much, so a drag over bare canvas leaves no undo step.</summary>
    [Fact]
    public void ARubThatUncoveredNothing_SaysSo()
    {
        var stroke = Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)).WithOwnMask(scale: 1);

        Assert.True(stroke.Mask!.Erase(new Point(50, 0), new Point(50, 0), 10));
        Assert.False(stroke.Mask.Erase(new Point(50, 0), new Point(50, 0), 10));
        Assert.False(stroke.Mask.Erase(new Point(9000, 0), new Point(9000, 0), 10));
    }

    /// <summary>A rubbed stroke keeps everything about itself. It is the same line, wearing less.</summary>
    [Fact]
    public void RubbingKeepsTheStrokeItself()
    {
        var stroke = Stroke(30, AnnotationTool.Highlighter, (0, 0), (100, 0));
        var rubbed = Rub(stroke, 50, 0, 6);

        Assert.Equal(stroke.Tool, rubbed.Tool);
        Assert.Equal(stroke.Color, rubbed.Color);
        Assert.Equal(stroke.Thickness, rubbed.Thickness);
        Assert.Equal(stroke.Opacity, rubbed.Opacity);
        Assert.Equal(stroke.Points, rubbed.Points);

        // And the original is untouched, which is what makes an erase drag undo in one press.
        Assert.Null(stroke.Mask);
    }

    /// <summary>
    /// The copy a drag works on must not write through to the stroke in the undo history.
    /// </summary>
    [Fact]
    public void RubbingACopy_LeavesTheOneItWasCopiedFrom()
    {
        var first = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)), 25, 0, 8);

        var second = first.WithOwnMask(scale: 1);
        second.Mask!.Erase(new Point(75, 0), new Point(75, 0), 8);

        Assert.False(InkAt(second, 25, 0));   // the earlier bite came along
        Assert.False(InkAt(second, 75, 0));   // and so did the new one
        Assert.True(InkAt(first, 75, 0));     // but the older stroke never saw it
    }
}
