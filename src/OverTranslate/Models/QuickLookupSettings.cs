namespace OverTranslate.Models;

/// <summary>
/// Everything 取詞翻譯 keeps between lookups, under one key.
/// </summary>
public class QuickLookupSettings
{
    /// <summary>
    /// Whether each successful translation replaces the clipboard contents. Off by default because
    /// copying without an explicit gesture is useful only when the reader has chosen that workflow.
    /// </summary>
    public bool AutoCopyTranslation { get; set; } = false;
}
