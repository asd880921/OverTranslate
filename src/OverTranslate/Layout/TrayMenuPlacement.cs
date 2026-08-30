using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// Places the tray menu beside the pointer, inside that monitor's physical-pixel bounds.
/// </summary>
/// <remarks>
/// The bounds, not the work area — the pointer is on a tray icon, and a tray icon is on the
/// taskbar, which the work area excludes. See TrayMenuWindow.PositionWindow.
/// </remarks>
internal static class TrayMenuPlacement
{
    /// <param name="monitor">
    /// The region the menu has to stay inside: the whole monitor, taskbar included.
    /// </param>
    public static (int Left, int Top) Place(
        Point pointer,
        Size windowSize,
        Rect monitor,
        double gap,
        double contentInset)
    {
        var contentWidth = Math.Max(0, windowSize.Width - contentInset * 2);
        var contentHeight = Math.Max(0, windowSize.Height - contentInset * 2);
        var minX = monitor.Left + gap;
        var maxX = Math.Max(minX, monitor.Right - contentWidth - gap);
        var minY = monitor.Top + gap;
        var maxY = Math.Max(minY, monitor.Bottom - contentHeight - gap);

        var bestLeft = 0.0;
        var bestTop = 0.0;
        var bestDistance = double.MaxValue;

        // Prefer above-left when multiple corners can touch the pointer exactly, matching the
        // familiar direction of a tray context menu. Clamping naturally selects another corner
        // when the pointer is beside the top, left or right taskbar instead.
        var corners = new[]
        {
            (Right: false, Bottom: true),
            (Right: true, Bottom: true),
            (Right: false, Bottom: false),
            (Right: true, Bottom: false)
        };

        foreach (var corner in corners)
        {
            var left = Math.Clamp(
                pointer.X - (corner.Right ? contentWidth : 0), minX, maxX);
            var top = Math.Clamp(
                pointer.Y - (corner.Bottom ? contentHeight : 0), minY, maxY);
            var cornerX = left + (corner.Right ? contentWidth : 0);
            var cornerY = top + (corner.Bottom ? contentHeight : 0);
            var distance = Math.Pow(pointer.X - cornerX, 2) + Math.Pow(pointer.Y - cornerY, 2);

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestLeft = left;
            bestTop = top;
        }

        return (
            (int)Math.Round(bestLeft - contentInset),
            (int)Math.Round(bestTop - contentInset));
    }
}
