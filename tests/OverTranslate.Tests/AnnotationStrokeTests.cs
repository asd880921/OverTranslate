using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the 標記 eraser can reach. The rest of the feature is a window and is checked by eye; this
/// is the one part with arithmetic in it that can be silently wrong.
/// </summary>
public class AnnotationStrokeTests
{
    private static AnnotationStroke Stroke(double thickness, params (double X, double Y)[] points) =>
        new()
        {
            Tool      = AnnotationTool.Pen,
            Color     = Colors.Orange,
            Thickness = thickness,
            Points    = [.. points.Select(p => new Point(p.X, p.Y))],
        };

    [Fact]
    public void PointerOnTheStroke_Hits()
    {
        var stroke = Stroke(4, (0, 0), (100, 0));
        Assert.True(stroke.IsWithin(new Point(50, 0), radius: 1));
    }

    [Fact]
    public void PointerWellClearOfTheStroke_Misses()
    {
        var stroke = Stroke(4, (0, 0), (100, 0));
        Assert.False(stroke.IsWithin(new Point(50, 40), radius: 6));
    }

    /// <summary>
    /// The reason the hit test walks segments rather than recorded points: a fast stroke is a handful
    /// of points a long way apart, and most of the line the user can see is between them.
    /// </summary>
    [Fact]
    public void PointerBetweenTwoDistantRecordedPoints_Hits()
    {
        var stroke = Stroke(2, (0, 0), (400, 0));
        Assert.True(stroke.IsWithin(new Point(200, 0), radius: 3));
    }

    /// <summary>A thick stroke is easier to hit, because the user is aiming at what they can see.</summary>
    [Fact]
    public void ThicknessWidensTheReach()
    {
        var target = new Point(50, 9);
        Assert.False(Stroke(4,  (0, 0), (100, 0)).IsWithin(target, radius: 5));
        Assert.True(Stroke(10, (0, 0), (100, 0)).IsWithin(target, radius: 5));
    }

    /// <summary>Past the end of a segment the distance is to its endpoint, not to its infinite line.</summary>
    [Fact]
    public void PointerBeyondTheEnd_MeasuresFromTheEndpoint()
    {
        var stroke = Stroke(2, (0, 0), (100, 0));
        Assert.True(stroke.IsWithin(new Point(103, 0), radius: 5));
        Assert.False(stroke.IsWithin(new Point(130, 0), radius: 5));
    }

    /// <summary>A tap is one point and still has to be erasable.</summary>
    [Fact]
    public void SinglePointStroke_IsHitTestable()
    {
        var stroke = Stroke(6, (20, 20));
        Assert.True(stroke.IsWithin(new Point(22, 20), radius: 2));
        Assert.False(stroke.IsWithin(new Point(60, 20), radius: 2));
    }

    /// <summary>
    /// Two pointer events at the same spot make a zero-length segment, which has no direction to
    /// project onto. It must not divide by zero, and it must still be reachable.
    /// </summary>
    [Fact]
    public void RepeatedPoint_DoesNotBreakTheProjection()
    {
        var stroke = Stroke(2, (10, 10), (10, 10), (10, 10));
        Assert.True(stroke.IsWithin(new Point(10, 10), radius: 1));
        Assert.False(stroke.IsWithin(new Point(40, 10), radius: 1));
    }

    /// <summary>The highlighter is see-through and the pen is not; nothing else about them differs.</summary>
    [Fact]
    public void HighlighterIsTheOnlyTranslucentTool()
    {
        Assert.Equal(1.0, Stroke(4, (0, 0)).Opacity);
        Assert.True(new AnnotationStroke
        {
            Tool      = AnnotationTool.Highlighter,
            Color     = Colors.Yellow,
            Thickness = 20,
            Points    = [new Point(0, 0)],
        }.Opacity < 1.0);
    }
}
