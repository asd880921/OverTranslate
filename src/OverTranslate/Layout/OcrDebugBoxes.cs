using System.Windows;
using OverTranslate.Services;

namespace OverTranslate.Layout;

/// <summary>
/// The two sets of boxes the OCR debug overlay draws: what the recogniser returned, and what the
/// grouper joined those into.
/// </summary>
/// <remarks>
/// Both can be shown at once, and they nest — a group is made of its lines, and a group of one line
/// is that line. Drawn at the same rectangle the pair would be unreadable exactly where reading it
/// matters, so the group box is pushed out to enclose its lines rather than coincide with them.
/// That is also what it means: everything inside this went to the translator as one request.
/// </remarks>
internal static class OcrDebugBoxes
{
    /// <summary>
    /// How far a group box sits outside the lines it holds, in the physical pixels every other
    /// block coordinate is measured in. Enough to stay a separate edge at any scale the overlay
    /// runs at, small enough not to reach a neighbouring group.
    /// </summary>
    public const double GroupOutset = 3;

    public static List<Rect> LineBoxes(IReadOnlyList<OcrTextBlock> blocks) =>
        blocks.SelectMany(block => block.Lines).ToList();

    public static List<Rect> GroupBoxes(IReadOnlyList<OcrTextBlock> blocks) =>
        blocks.Select(block => Rect.Inflate(block.Bounds, GroupOutset, GroupOutset)).ToList();
}
