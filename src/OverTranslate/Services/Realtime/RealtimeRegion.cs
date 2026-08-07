using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// One user-drawn watch area, in physical screen pixels — the same coordinate space the screen grab
/// and the OCR boxes live in, so nothing on the recognition path has to know about DPI.
/// </summary>
/// <param name="Id">
/// Stable for as long as the block exists, so a result arriving after the user has re-entered edit
/// mode and rearranged things can be matched to the window it belongs to (or dropped).
/// </param>
public sealed record RealtimeRegion(int Id, Rectangle Bounds);

/// <summary>
/// The translated lines currently showing for one region. An empty list is a real result — it means
/// the region no longer holds any readable text — and clears the overlay rather than leaving the
/// previous subtitle stranded on screen.
/// </summary>
public sealed record RealtimeRegionUpdate(int RegionId, IReadOnlyList<TranslatedBlock> Lines);
