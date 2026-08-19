namespace OverTranslate.Views.Controls;

/// <summary>
/// The two icon-font glyphs every speak button in the application shares.
/// </summary>
/// <remarks>
/// In one place because there are now two of these buttons - the translation page's pair and the
/// capture toolbar's - and a user who meets both should not find the same action drawn two ways.
/// Segoe MDL2 codepoints, so they survive Windows 10, where Segoe Fluent Icons is not installed.
///
/// Which of the two shows is the action, not the state: a button reading text aloud offers to stop,
/// the same way the realtime bar's pause button offers what pressing it would do.
/// </remarks>
internal static class TtsGlyphs
{
    /// <summary>Speaker - start reading.</summary>
    public const string Speak = "\uE767";

    /// <summary>Stop - end the reading this button started.</summary>
    public const string Stop = "\uE71A";
}
