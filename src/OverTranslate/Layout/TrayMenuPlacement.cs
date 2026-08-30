using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// Places the tray menu beside the pointer and inside that monitor's physical-pixel work area.
/// </summary>
internal static class TrayMenuPlacement
{
    public static (int Left, int Top) Place(Point pointer, Size size, Rect workArea, double gap)
    {
        var minX = workArea.Left + gap;
        var maxX = Math.Max(minX, workArea.Right - size.Width - gap);
        var minY = workArea.Top + gap;
        var maxY = Math.Max(minY, workArea.Bottom - size.Height - gap);

        var left = Math.Clamp(pointer.X, minX, maxX);
        var above = pointer.Y - size.Height;
        var top = above >= minY
            ? Math.Min(above, maxY)
            : Math.Clamp(pointer.Y + gap, minY, maxY);

        return ((int)Math.Round(left), (int)Math.Round(top));
    }
}
