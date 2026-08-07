using System.Drawing;
using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Picks the lines of a capture worth reading a second time, and works out what to read.
/// </summary>
/// <remarks>
/// A recognition is deterministic: the same pixels through the same pipeline give the same answer,
/// wrong characters included. What changes the answer is changing the input, and cropping one line
/// out of a capture changes it thoroughly — the crop is small enough to skip the downscale a whole
/// capture goes through, its dimensions quantise differently, and the detector frames the line again
/// from scratch. Measured elsewhere this session, those are exactly the variables that turned an
/// unreadable line into a perfect one.
///
/// Which lines are worth it comes from the scores. Over 29 large captures (688 lines) the median
/// line scored between 0.94 and 1.00, while the bottom of every capture was either noise or plainly
/// garbled — "BanG Draisds ofel" for "BanG Dream!", "[발리빨리" for "빨리빨리". The floor is set
/// under that band rather than through it: a correct Korean line scored 0.82, so anything higher
/// would start re-reading text that was already right.
///
/// The cost is what makes this worth doing at all. Recognition is charged per line — measured at
/// roughly 40ms each on top of 100-400ms of detection — so re-reading a whole capture would double
/// a 3.5 second wait, while re-reading the doubtful lines of one costs 2.9 lines on average, 12 at
/// worst, which is under a fifth of a second with the engine's existing concurrency.
///
/// WHAT THIS DOES NOT REACH. Confidence separates grossly broken readings from good ones, and
/// nothing finer. Rendering a subtitle into captures of three different sizes produced
/// "displlited", "displtted" and "displfted" for "dispirited" — every one of them scored between
/// 0.89 and 0.95, comfortably above this floor, because the model is not hesitating over that
/// character, it is confidently reading an l. Nor would re-reading rescue it: the same line was
/// tried at ten different scales and croppings earlier and misread in all ten. A confusion between
/// glyphs that look alike belongs to the recognition model, and raising the floor to chase it would
/// only spend time re-reading lines that were already right.
/// </remarks>
internal static class DoubtfulBlocks
{
    /// <summary>Below this a line is worth reading again. See the remarks for where it comes from.</summary>
    public const double ConfidenceFloor = 0.85;

    /// <summary>
    /// Most lines re-read from one capture, so the worst case stays bounded. The measured worst was
    /// 12 lines out of 86; past this the capture is doubtful enough that another pass over a few
    /// more lines would not rescue it.
    /// </summary>
    public const int MaxRereads = 8;

    /// <summary>
    /// Margin around a line, as a fraction of its height. The detector wants some space to find an
    /// edge in — handed a crop cut exactly to the glyphs it tends to find nothing at all — and this
    /// is small enough that the line above or below does not come with it.
    /// </summary>
    public const double CropMargin = 0.4;

    /// <summary>
    /// Indices of the lines to re-read, least confident first, so the cap spends itself on the
    /// worst. Lines with no score are left alone: the engine reports none when it had none to give,
    /// which is not the same as reporting a bad one.
    /// </summary>
    public static List<int> Select(IReadOnlyList<OcrTextBlock> blocks)
    {
        var doubtful = new List<(int Index, double Confidence)>();

        for (var index = 0; index < blocks.Count; index++)
            if (blocks[index].Confidence is { } confidence && confidence < ConfidenceFloor)
                doubtful.Add((index, confidence));

        return doubtful
            .OrderBy(block => block.Confidence)
            .Take(MaxRereads)
            .Select(block => block.Index)
            .ToList();
    }

    /// <summary>
    /// The rectangle to cut for a line, clamped to the capture. Empty when the line lies outside it.
    /// </summary>
    public static Rectangle CropAround(Rect bounds, int captureWidth, int captureHeight)
    {
        var margin = Math.Max(6, bounds.Height * CropMargin);

        var crop = Rectangle.FromLTRB(
            (int)Math.Floor(bounds.Left - margin),
            (int)Math.Floor(bounds.Top - margin),
            (int)Math.Ceiling(bounds.Right + margin),
            (int)Math.Ceiling(bounds.Bottom + margin));

        return Rectangle.Intersect(crop, new Rectangle(0, 0, captureWidth, captureHeight));
    }

    /// <summary>
    /// Whether something found in a crop is the line the crop was cut for, rather than a neighbour
    /// the margin caught the edge of. Compared by centre, because the re-read box is framed
    /// independently and will not share an edge with the original.
    /// </summary>
    public static bool IsSameLine(Rect original, Rect candidate, Rectangle crop)
    {
        // The candidate is in the crop's own coordinates; the original is in the capture's.
        var centre = crop.Top + candidate.Top + candidate.Height / 2;
        return centre >= original.Top && centre <= original.Bottom;
    }
}
