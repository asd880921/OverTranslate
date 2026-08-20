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
    /// The two limits are the range the window may occupy, not the work area. They are not the same
    /// thing: this window is larger than the card it draws by a transparent margin the shadow fades
    /// out into, and those pixels are nothing. Measured against the work area itself, the bottom of
    /// every screen would be refused to a card that could plainly still go there — which is a gap
    /// the user can see and cannot explain. The caller is what subtracts the margin.
    ///
    /// A popup taller than the range cannot be contained. It is pinned to the top, which keeps the
    /// box and the beginning of the result readable and loses the end off the bottom — the opposite
    /// way round from doing nothing.
    /// </remarks>
    /// <param name="currentTop">Where the window is now, in physical pixels.</param>
    /// <param name="restingTop">Where it belongs when nothing is lifting it, or null if that is where it already is.</param>
    /// <param name="height">The height the window is taking, in physical pixels, shadow margin included.</param>
    /// <param name="limitTop">Lowest top the window may have — the work area's top, less the margin above the card.</param>
    /// <param name="limitBottom">Highest bottom the window may have — the work area's bottom, plus the margin below the card.</param>
    /// <returns>
    /// The top to move to, and the resting top to carry forward — null once the window fits where it
    /// belongs, which is what stops a later collapse from moving a window nobody asked to move.
    /// </returns>
    public static (int Top, int? RestingTop) Place(
        int currentTop, int? restingTop, int height, int limitTop, int limitBottom)
    {
        int resting = restingTop ?? currentTop;

        // Math.Clamp throws when the popup is taller than the range has room for.
        int maxTop = Math.Max(limitTop, limitBottom - height);

        int top = Math.Clamp(resting, limitTop, maxTop);

        return (top, top < resting ? resting : null);
    }
}
