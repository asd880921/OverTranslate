using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the eraser actually takes away. The claim being checked is that it is a circle: the ring
/// drawn under the pointer is a promise about what will disappear, and these say the promise is kept.
/// </summary>
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
        stroke.WithErased(AnnotationStroke.SweptCircle([new Point(x, y)], radius));

    [Fact]
    public void ARubInTheMiddle_TakesTheMiddleAndLeavesTheEnds()
    {
        var rubbed = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)), 50, 0, 10);

        Assert.False(rubbed.Painted.FillContains(new Point(50, 0)));
        Assert.True(rubbed.Painted.FillContains(new Point(5, 0)));
        Assert.True(rubbed.Painted.FillContains(new Point(95, 0)));
    }

    /// <summary>
    /// The test the whole design turns on. A small eraser run down the middle of a wide highlight has
    /// to take a small bite out of it — not the entire band, which is what cutting the centre line
    /// would do, and not nothing.
    /// </summary>
    [Fact]
    public void ASmallRubOnAWideStroke_TakesOnlyWhatTheCircleCovered()
    {
        var stroke = Stroke(30, AnnotationTool.Highlighter, (0, 0), (100, 0));
        var rubbed = Rub(stroke, 50, 0, 6);

        // Gone where the circle was...
        Assert.False(rubbed.Painted.FillContains(new Point(50, 0)));

        // ...and still there directly above and below it, inside the same 30px band.
        Assert.True(rubbed.Painted.FillContains(new Point(50, 12)));
        Assert.True(rubbed.Painted.FillContains(new Point(50, -12)));
    }

    [Fact]
    public void ARubThatCoversEverything_LeavesNothing()
    {
        var rubbed = Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (10, 0)), 5, 0, 60);
        Assert.True(rubbed.IsErasedAway);
    }

    /// <summary>
    /// Checked by asking where the ink is rather than by comparing areas: subtracting one geometry
    /// from another flattens its curves, so even a rub that removes nothing comes back a few
    /// thousandths of a unit lighter. That difference is real and is not the question.
    /// </summary>
    [Fact]
    public void ARubNowhereNearIt_LeavesTheStrokeWhole()
    {
        var stroke = Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0));
        var rubbed = Rub(stroke, 50, 400, 10);

        Assert.False(rubbed.IsErasedAway);
        foreach (double x in new[] { 1.0, 25.0, 50.0, 75.0, 99.0 })
            Assert.True(rubbed.Painted.FillContains(new Point(x, 0)), $"ink missing at x={x}");
    }

    /// <summary>
    /// A drag is sampled, so the swept area has to be the circle's whole path and not a string of
    /// circles with gaps between them. Two points 200 apart, and the middle must still be covered.
    /// </summary>
    [Fact]
    public void ADragBetweenTwoDistantPoints_SweepsTheWholeWay()
    {
        var sweep = AnnotationStroke.SweptCircle([new Point(0, 0), new Point(200, 0)], 10);

        Assert.True(sweep.FillContains(new Point(100, 0)));
        Assert.True(sweep.FillContains(new Point(100, 8)));
        Assert.False(sweep.FillContains(new Point(100, 40)));
    }

    /// <summary>Rubbing twice takes both bites, not just the second.</summary>
    [Fact]
    public void TwoRubs_BothStick()
    {
        var rubbed = Rub(Rub(Stroke(4, AnnotationTool.Pen, (0, 0), (100, 0)), 25, 0, 8), 75, 0, 8);

        Assert.False(rubbed.Painted.FillContains(new Point(25, 0)));
        Assert.False(rubbed.Painted.FillContains(new Point(75, 0)));
        Assert.True(rubbed.Painted.FillContains(new Point(50, 0)));
    }

    /// <summary>A rubbed stroke keeps everything about itself except its shape.</summary>
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

        // And the original is untouched, which is what makes an erase drag undoable in one press.
        Assert.Null(stroke.Carved);
        Assert.True(stroke.Painted.FillContains(new Point(50, 0)));
    }
}
