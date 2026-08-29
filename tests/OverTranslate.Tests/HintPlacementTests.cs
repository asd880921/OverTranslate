using System.Windows;
using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Where 快速翻譯's hint lands, which is the only thing telling the user their shortcut did
/// anything.
/// </summary>
/// <remarks>
/// A 1080p desktop with the taskbar taken off the bottom, and a card the size the hint actually is
/// with one line of text in it.
/// </remarks>
public class HintPlacementTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size Card = new(240, 60);
    private const double Gap = 6;

    private static (int Left, int Top) Place(Rect? anchor, Point pointer) =>
        HintPlacement.Place(anchor, pointer, Card, WorkArea, Gap);

    [Fact]
    public void ASelectionPutsTheCardAboveIt_Centred()
    {
        var selection = new Rect(800, 500, 200, 20);

        var (left, top) = Place(selection, new Point(0, 0));

        // Centred on the selection: 800 + 100 - 120.
        Assert.Equal(780, left);
        Assert.Equal(500 - 60 - Gap, top);
    }

    [Fact]
    public void ASelectionOnTheTopLine_PutsTheCardUnderneathInstead()
    {
        // There is no room above, and the alternative to going under it is a card clamped to the top
        // edge — sitting over the very text it is reporting on.
        var selection = new Rect(800, 4, 200, 20);

        var (_, top) = Place(selection, new Point(0, 0));

        Assert.Equal(selection.Bottom + Gap, top);
    }

    [Fact]
    public void ASelectionAtTheEdgeOfTheScreen_KeepsTheWholeCardOnIt()
    {
        var selection = new Rect(1880, 500, 30, 20);

        var (left, _) = Place(selection, new Point(0, 0));

        Assert.Equal(WorkArea.Right - Card.Width - Gap, left);
    }

    [Fact]
    public void WithNoSelection_TheCardSitsUnderTheRightOfThePointer()
    {
        // Down and to the right, where the pointer's own hotspot cannot cover it.
        var (left, top) = Place(null, new Point(400, 300));

        Assert.Equal(412, left);
        Assert.Equal(312, top);
    }

    [Fact]
    public void APointerInTheBottomRightCorner_StillLeavesTheCardOnScreen()
    {
        var (left, top) = Place(null, new Point(1915, 1035));

        Assert.Equal(WorkArea.Right - Card.Width - Gap, left);
        Assert.Equal(WorkArea.Bottom - Card.Height - Gap, top);
    }

    [Fact]
    public void ACardWiderThanTheScreenIsPinnedToTheNearEdge_RatherThanThrowing()
    {
        // Math.Clamp throws when the lower bound is above the upper one, and a long enough failure
        // message on a small enough monitor gets there.
        var (left, top) = HintPlacement.Place(
            new Rect(10, 10, 20, 20), new Point(0, 0), new Size(3000, 2000), WorkArea, Gap);

        Assert.Equal(Gap, left);
        Assert.Equal(Gap, top);
    }
}
