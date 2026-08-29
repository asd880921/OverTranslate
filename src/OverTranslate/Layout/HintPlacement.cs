using System.Windows;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide.
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// Where 快速翻譯's hint goes: by the text it is about, or by the pointer when nothing will say
/// where that text is.
/// </summary>
/// <remarks>
/// The hint is the only thing telling the user that a shortcut they pressed over someone else's
/// window did anything at all, so it has to land where they are already looking. Above the selection
/// rather than over it: what they are reading is the text underneath, and the reply to a request
/// about it must not be the thing that hides it.
///
/// Everything here is physical pixels on one monitor's work area, and every value it is given has
/// already been scaled for that monitor — see <see cref="Services.ScreenGeometry"/>. It computes and
/// does not measure, so the choice it makes can be tested.
/// </remarks>
internal static class HintPlacement
{
    /// <summary>
    /// The hint's top-left corner.
    /// </summary>
    /// <param name="anchor">
    /// The selection, or null when nothing would say where it is.
    /// </param>
    /// <param name="pointer">Where the pointer is, which is the fallback anchor.</param>
    /// <param name="size">The hint window's size.</param>
    /// <param name="workArea">The monitor being placed on, taskbar already taken off.</param>
    /// <param name="gap">
    /// The breathing room between the hint and whatever it is placed against, and the closest it may
    /// come to the edge of the screen.
    /// </param>
    public static (int Left, int Top) Place(
        Rect? anchor, Point pointer, Size size, Rect workArea, double gap)
    {
        var (left, top) = anchor is { } selection
            ? BySelection(selection, size, workArea, gap)
            : ByPointer(pointer, gap);

        return (
            (int)Math.Round(Clamp(left, workArea.Left, workArea.Right - size.Width, gap)),
            (int)Math.Round(Clamp(top, workArea.Top, workArea.Bottom - size.Height, gap)));
    }

    /// <remarks>
    /// Centred on the selection and above it, which is where a reader's eye already is at the end of
    /// the gesture that made the selection. Below it when there is no room above — a selection on the
    /// first line of a maximised window has none — because the alternative is a hint clamped to the
    /// top edge, sitting over the text it is about.
    /// </remarks>
    private static (double Left, double Top) BySelection(
        Rect selection, Size size, Rect workArea, double gap)
    {
        var left = selection.Left + selection.Width / 2 - size.Width / 2;
        var above = selection.Top - size.Height - gap;

        return (left, above >= workArea.Top + gap ? above : selection.Bottom + gap);
    }

    /// <remarks>
    /// Down and to the right, the way every tooltip on this platform sits: the pointer's hotspot is
    /// its top-left corner, so that is the one direction where the hint cannot come out from under
    /// the cursor itself.
    /// </remarks>
    private static (double Left, double Top) ByPointer(Point pointer, double gap) =>
        (pointer.X + gap * 2, pointer.Y + gap * 2);

    /// <summary>Keeps a coordinate inside the work area, <paramref name="gap"/> off the edge.</summary>
    /// <remarks>
    /// The lower bound wins when the hint is larger than the space it has to fit in, which
    /// <see cref="Math.Clamp"/> would throw over. Against the near edge is the better of the two
    /// wrong answers: it is the corner the reader is already facing.
    /// </remarks>
    private static double Clamp(double value, double min, double max, double gap) =>
        Math.Max(min + gap, Math.Min(value, Math.Max(min + gap, max - gap)));
}
