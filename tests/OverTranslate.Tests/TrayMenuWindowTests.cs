using Xunit;
using OverTranslate.Layout;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// The tray menu behaves like a context menu even though it is implemented as a WPF window.
/// </summary>
public class TrayMenuWindowTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Shell", file));

    [Fact]
    public void The_menu_uses_the_pointer_monitor_and_physical_pixels()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("Screen.FromPoint", source);
        Assert.Contains("ScreenGeometry.ScaleAt", source);
        Assert.Contains("ScreenGeometry.MoveToPhysical", source);
        Assert.DoesNotContain("SystemParameters.WorkArea", source);
    }

    [Fact]
    public void Showing_the_menu_activates_it_so_clicking_elsewhere_deactivates_and_closes_it()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("Activate();", source);
        Assert.Contains("Deactivated", source);
    }

    [Fact]
    public void A_left_taskbar_keeps_the_menu_inside_the_remaining_work_area()
    {
        var workArea = new Rect(48, 0, 1872, 1080);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(20, 600), new Size(240, 180), workArea, 4);

        Assert.Equal(52, left);
        Assert.Equal(420, top);
    }

    [Fact]
    public void A_top_taskbar_puts_the_menu_below_it_when_there_is_no_room_above()
    {
        var workArea = new Rect(0, 48, 1920, 1032);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(900, 20), new Size(240, 180), workArea, 4);

        Assert.Equal(900, left);
        Assert.Equal(52, top);
    }

    [Fact]
    public void A_pointer_on_a_negative_coordinate_monitor_stays_on_that_monitor()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(-10, 1030), new Size(240, 180), workArea, 4);

        Assert.Equal(-244, left);
        Assert.Equal(850, top);
    }
}
