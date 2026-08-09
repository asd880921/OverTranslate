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
/// <param name="Mode">
/// What the user says this block holds. Lives here rather than in the settings file because it
/// belongs to one block of one session: the same user watches a subtitle strip and a game panel at
/// once, and the answer is different for each. Defaults to <see cref="RealtimeBlockMode.Subtitle"/>,
/// which is what the great majority of blocks are.
/// </param>
public sealed record RealtimeRegion(
    int Id, Rectangle Bounds, RealtimeBlockMode Mode = RealtimeBlockMode.Subtitle);

/// <summary>
/// One block as the user has it arranged, before a session gives it an id — what edit mode hands
/// back and what the controller keeps between edits, so re-entering edit mode restores the modes
/// along with the rectangles.
/// </summary>
public sealed record RealtimeBlockPlacement(
    Rectangle Bounds, RealtimeBlockMode Mode = RealtimeBlockMode.Subtitle);

/// <summary>
/// The translated lines currently showing for one region. An empty list is a real result — it means
/// the region no longer holds any readable text — and clears the overlay rather than leaving the
/// previous subtitle stranded on screen.
/// </summary>
public sealed record RealtimeRegionUpdate(
    int RegionId,
    IReadOnlyList<TranslatedBlock> Lines,
    int RefreshGeneration);
