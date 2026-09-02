namespace OverTranslate.Services.Ocr;

/// <summary>
/// Whether the picture itself says two lines belong to different things, whatever their geometry
/// says.
/// </summary>
/// <remarks>
/// <para>Geometry answers "could these be one block of text?". It cannot answer "do they look like
/// one?", and on a laid-out screen the second question is often the only one with an answer. A
/// heading over the box it labels and a sentence over its own second line are the same two
/// rectangles at the same spacing; what separates them is that the heading is blue on grey and the
/// box under it is black on white.</para>
///
/// <para>This exists because the alternative was another number. The rule in
/// <see cref="OcrTextBlockGrouper"/> that admits a shorter following line has no leading test, and
/// a bar of 0.45 of a line would have fixed the diagram that prompted this while breaking a
/// measured Korean subtitle whose real leading is 0.71 — Hangul boxes sit tight on glyphs with no
/// ascenders or descenders, so the same wrap measures looser than it does in Latin. Tuning past
/// that meant separating a width ratio of 2.12 from one of 2.27, which is fitting to one sample.
/// Colour is not a number of that kind: it is in the capture, it is what the designer used to say
/// these are different components, and it means the same thing in every script.</para>
///
/// <para>Negative evidence only, and only where the geometry was already going to merge. Colour
/// cannot say two lines belong together — a link, a bold word or a highlighted term makes one
/// sentence two colours — so a difference may refuse a merge and a similarity may never force one.</para>
/// </remarks>
internal static class VisualSplitEvidence
{
    /// <summary>
    /// How far apart two backgrounds must look before they are different surfaces rather than one
    /// surface sampled twice.
    /// </summary>
    /// <remarks>
    /// <para>About the smallest difference an eye reports at all — a CIELAB distance of 2.3 is the
    /// usual figure for that, and this sits just under it. It can be that low because a flat
    /// surface sampled twice does not drift: measured across ten captures, every pair of lines
    /// sharing a background came back between 0.0 and 0.4, and the first pair that did not share
    /// one came back at 2.4. There is nothing in between to cut through.</para>
    ///
    /// <para>2.4 is the whole grey-card-against-white-content difference, which is smaller than it
    /// sounds because the ring sampled around a line near a card's edge holds some of both. That is
    /// also why this cannot be the only signal: at 2.4 it is under several pairs that do share a
    /// surface and only split on geometry, so on its own it would refuse merges that were right.</para>
    /// </remarks>
    public const double BackgroundDifference = 2.0;

    /// <summary>
    /// How far apart two text colours must look before they are different inks rather than one ink
    /// sampled twice.
    /// </summary>
    /// <remarks>
    /// Well clear of the noise and well under the real thing: across the same captures, two lines
    /// written in one colour measured 0.0 to 3.6 apart, and blue heading against black body
    /// measured 116. The bar is nowhere near either. What it must NOT be read as is a bar that
    /// separates right from wrong on its own — Wikipedia body text is full of blue links, and a
    /// line whose dominant ink is a link over a line whose dominant ink is black measures 68 while
    /// being one sentence that has to stay whole. That pair is spared by the background, not by
    /// this.
    /// </remarks>
    public const double ForegroundDifference = 25.0;

    /// <summary>
    /// Whether the two lines look like parts of different components.
    /// </summary>
    /// <remarks>
    /// <para>Both signals, because each on its own is wrong on real captures and they fail in
    /// opposite directions. Measured:</para>
    ///
    /// <list type="bullet">
    /// <item>a blue heading on grey over black content on white — 2.4 and 116 — must split;</item>
    /// <item>Wikipedia prose whose line ends in a link — 0.0 and 68 — must stay joined, so the ink
    /// alone would break it;</item>
    /// <item>a poster's two lines over a photograph — 99.7 and 2.6 — must stay joined, so the
    /// surface alone would break it.</item>
    /// </list>
    ///
    /// <para>Requiring both is also the honest reading of what the signal means. A designer who
    /// puts a heading on its own surface nearly always gives it its own colour as well; text that
    /// merely runs past a card's edge, or is written in a script whose links happen to be coloured,
    /// changes one and not the other.</para>
    /// </remarks>
    public static bool IsStrong(BlockAppearance previous, BlockAppearance current) =>
        PerceptualColor.Distance(previous.Background, current.Background) >= BackgroundDifference &&
        PerceptualColor.Distance(previous.Foreground, current.Foreground) >= ForegroundDifference;
}
