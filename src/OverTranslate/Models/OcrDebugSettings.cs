namespace OverTranslate.Models;

/// <summary>
/// What the OCR debug overlay draws over a screenshot translation, so it can be seen why a capture
/// was read and grouped the way it was.
/// </summary>
/// <remarks>
/// <para>Both off, and there is no third key switching the pair on: two boxes that draw nothing when
/// neither is ticked need no master, and a master is a way to end up with a feature that is on but
/// invisible. Off matters here beyond taste — this ships into existing installs, and nobody should
/// update the app and find boxes drawn over their translations.</para>
///
/// <para>Grouped rather than left loose in <see cref="AppSettings"/> for the reason
/// <see cref="RealtimeSettings"/> gives: the flat keys there are flat because they shipped that way,
/// and anything new belongs under the thing that owns it. Whether the card in 設定 is open is not
/// here on purpose — it opens shut on every visit, because this is not something to leave running
/// and the page should not look as though it is.</para>
/// </remarks>
public class OcrDebugSettings
{
    /// <summary>Outlines each box the recogniser returned.</summary>
    public bool ShowLineBoxes { get; set; } = false;

    /// <summary>Outlines each group those boxes were joined into — one translation request each.</summary>
    public bool ShowGroupBoxes { get; set; } = false;
}
