namespace OverTranslate.Models;

/// <summary>
/// Everything 取詞翻譯 keeps between sittings, under one key.
/// </summary>
/// <remarks>
/// A group rather than more flat keys, following <see cref="RealtimeSettings"/> — see the note at
/// the foot of <see cref="AppSettings"/> for why every section added from here on is grouped.
///
/// Small on purpose, and it should stay that way. The languages and the translation service this
/// popup runs on are deliberately absent: it reads the same three fields 截圖翻譯 and 文字翻譯 do,
/// so a service switched here is switched everywhere, and someone who set their target language
/// once never has to answer that question again in a window the size of a search box.
/// </remarks>
public class QuickLookupSettings
{
    /// <summary>
    /// Whether a finished translation is read aloud without being asked.
    /// </summary>
    /// <remarks>
    /// Off by default. This popup opens over whatever the user is doing — a call, a video, a game —
    /// and a default that makes noise there is one the user has to discover in order to switch off.
    /// Worth offering at all because looking a word up and hearing it is one action for the people
    /// who use this to read a foreign language, not two.
    /// </remarks>
    public bool AutoSpeakResult { get; set; }
}
