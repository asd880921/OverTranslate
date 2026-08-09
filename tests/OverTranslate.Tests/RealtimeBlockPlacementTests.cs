using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeBlockPlacementTests
{
    [Fact]
    public void GuidanceStartsExpandedForANewPlacement()
    {
        var placement = new RealtimeBlockPlacement(new Rectangle(10, 20, 300, 80));

        Assert.True(placement.GuidanceExpanded);
    }

    [Fact]
    public void GuidanceStateTravelsWithTheRectangleAndMode()
    {
        var collapsed = new RealtimeBlockPlacement(
            new Rectangle(10, 20, 300, 80),
            RealtimeBlockMode.Panel,
            GuidanceExpanded: false);

        var restored = collapsed with { };

        Assert.Equal(collapsed.Bounds, restored.Bounds);
        Assert.Equal(RealtimeBlockMode.Panel, restored.Mode);
        Assert.False(restored.GuidanceExpanded);
    }
}
