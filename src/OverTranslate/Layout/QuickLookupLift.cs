namespace OverTranslate.Layout;

/// <summary>
/// Keeps 取詞翻譯's popup on the screen while its result panel is open, and puts it back after.
/// </summary>
/// <remarks>
/// The popup is <c>SizeToContent="Height"</c>: the header stays where it is and a translation grows
/// out of the bottom of it. Summoned or dragged near the foot of the screen, the answer therefore
/// grows straight off the desktop — the user typed a word, something happened, and there is nothing
/// to read.
///
/// Lifting the whole window rather than growing it upwards. Growing the other way would move the box
/// the user is typing in, taking the field and its buttons up from under the pointer mid-sentence,
/// which is worse than moving a window nobody is touching.
/// </remarks>
internal static class QuickLookupLift
{
    /// <summary>
    /// Where the popup should sit at its current height, and the resting top to keep hold of.
    /// </summary>
    /// <remarks>
    /// The lift is a loan against a position the user chose, and <paramref name="restingTop"/> is
    /// what repays it. Judging the fit from where the window currently is would not work: a popup
    /// already lifted always fits where it has been lifted to, so it would never come back down, and
    /// every translation would walk it a little further up the screen.
    ///
    /// A popup taller than the work area cannot be contained. It is pinned to the top edge, which
    /// keeps the box and the beginning of the result readable and loses the end off the bottom —
    /// the opposite way round from doing nothing.
    /// </remarks>
    /// <param name="currentTop">Where the window is now, in physical pixels.</param>
    /// <param name="restingTop">Where it belongs when nothing is lifting it, or null if that is where it already is.</param>
    /// <param name="height">The window's current height, result panel and shadow margin included.</param>
    /// <param name="areaTop">Top of the work area it is on.</param>
    /// <param name="areaBottom">Bottom of that work area.</param>
    /// <param name="edge">Margin to leave against either edge.</param>
    /// <returns>
    /// The top to move to, and the resting top to carry forward — null once the window fits where it
    /// belongs, which is what stops a later collapse from moving a window nobody asked to move.
    /// </returns>
    public static (int Top, int? RestingTop) Place(
        int currentTop, int? restingTop, int height, int areaTop, int areaBottom, int edge)
    {
        int resting = restingTop ?? currentTop;

        // Math.Clamp throws when the popup is taller than the work area has room for.
        int minY = areaTop + edge;
        int maxY = Math.Max(minY, areaBottom - height - edge);

        int top = Math.Clamp(resting, minY, maxY);

        return (top, top < resting ? resting : null);
    }
}
