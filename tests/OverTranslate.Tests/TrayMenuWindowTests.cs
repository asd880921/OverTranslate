using Xunit;
using OverTranslate.Layout;
using OverTranslate.Views.Shell;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// The tray menu behaves like a context menu even though it is implemented as a WPF window.
/// </summary>
public class TrayMenuWindowTests
{
    private const double ShadowInset = 18;

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
    public void Clicking_the_menu_chrome_dismisses_it_without_treating_a_button_as_chrome()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("PreviewMouseDown", source);
        Assert.Contains("DismissOnNonButtonClick", source);
        Assert.Contains("IsInsideButton", source);
    }

    [Fact]
    public void A_button_and_its_content_are_not_menu_chrome()
    {
        OnUiThread(() =>
        {
            var label = new TextBlock();
            var button = new Button { Content = label };

            Assert.True(TrayMenuWindow.IsInsideButton(button));
            Assert.True(TrayMenuWindow.IsInsideButton(label));
            Assert.False(TrayMenuWindow.IsInsideButton(new Border()));
        });
    }

    [Fact]
    public void A_left_taskbar_keeps_the_menu_inside_the_remaining_work_area()
    {
        var workArea = new Rect(48, 0, 1872, 1080);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(20, 600), new Size(240, 180), workArea, 4, ShadowInset);

        Assert.Equal(34, left);
        Assert.Equal(438, top);
    }

    [Fact]
    public void A_top_taskbar_puts_the_menu_below_it_when_there_is_no_room_above()
    {
        var workArea = new Rect(0, 48, 1920, 1032);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(900, 20), new Size(240, 180), workArea, 4, ShadowInset);

        Assert.Equal(882, left);
        Assert.Equal(34, top);
    }

    [Fact]
    public void A_right_taskbar_uses_the_visible_bottom_right_corner()
    {
        var workArea = new Rect(0, 0, 1872, 1080);
        var window = new Size(240, 180);
        var pointer = new Point(1900, 600);

        var (left, top) = TrayMenuPlacement.Place(
            pointer, window, workArea, 4, ShadowInset);

        Assert.Equal(workArea.Right - 4, left + window.Width - ShadowInset);
        Assert.Equal(pointer.Y, top + window.Height - ShadowInset);
    }

    [Fact]
    public void A_pointer_on_a_negative_coordinate_monitor_stays_on_that_monitor()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);

        var (left, top) = TrayMenuPlacement.Place(
            new Point(-10, 1030), new Size(240, 180), workArea, 4, ShadowInset);

        Assert.Equal(-232, left);
        Assert.Equal(868, top);
    }

    [Fact]
    public void The_visible_corner_nearest_a_bottom_tray_icon_is_as_close_as_the_work_area_allows()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var window = new Size(276, 216);
        var pointer = new Point(400, 1060);

        var (left, top) = TrayMenuPlacement.Place(
            pointer, window, workArea, 4, ShadowInset);

        Assert.Equal(pointer.X, left + ShadowInset);
        Assert.Equal(workArea.Bottom - 4, top + window.Height - ShadowInset);
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
