namespace OverTranslate.Services.Ocr;

/// <summary>
/// Which writing system a block's own text is in, for the grouping geometry to reason about.
/// </summary>
/// <remarks>
/// Decided per block from the text that was read, never from the source language the user picked:
/// the same screenshot under 自動 and under 日文 must group identically, and the only way to get
/// there is for nothing on the layout side to know what was picked. An explicit field rather than
/// a null glyph height standing in for "this one is CJK", which is how the render metric ended up
/// serving two callers that wanted opposite things.
/// </remarks>
public enum OcrLayoutScript
{
    /// <summary>No text to judge from — punctuation or symbols only.</summary>
    Unknown,

    /// <summary>No CJK, and something to read: letters or digits.</summary>
    Latin,

    /// <summary>Kana or Han, and no Latin letters.</summary>
    Cjk,

    /// <summary>Both, which a line of Japanese technical prose routinely is.</summary>
    Mixed,
}

/// <summary>
/// Reads <see cref="OcrLayoutScript"/> off a recognised line.
/// </summary>
/// <remarks>
/// Any Han character counts, not two of them. Telling Chinese from Japanese needs the second one;
/// knowing the line is set in full-width glyphs does not, and that is all the layout side asks.
/// Two is also what left a single 攻 on the Latin geometry path — see #161.
///
/// Deliberately separate from <c>OnnxOcrEngine.UsesCjkLayoutForText</c>, which keeps its own rule
/// because it chooses a *render* normalisation strategy, not the script of the text.
/// </remarks>
internal static class LayoutScriptDetection
{
    public static OcrLayoutScript For(string text)
    {
        var hasCjk = false;
        var hasLatinLetter = false;
        var hasReadableGlyph = false;

        foreach (var c in text)
        {
            if (IsKana(c) || IsHanIdeograph(c))
            {
                hasCjk = true;
                hasReadableGlyph = true;
            }
            else if (char.IsLetter(c))
            {
                hasLatinLetter = true;
                hasReadableGlyph = true;
            }
            else if (char.IsDigit(c))
            {
                // Digits carry no script of their own, but a line of them is set in whatever the
                // surrounding interface uses, and today every "Lv100" and "23:29" already takes the
                // Latin path. Calling those Unknown would move them onto the coarse cross-script
                // fallback for no measured reason.
                hasReadableGlyph = true;
            }
        }

        return (hasCjk, hasLatinLetter, hasReadableGlyph) switch
        {
            (true, true, _) => OcrLayoutScript.Mixed,
            (true, false, _) => OcrLayoutScript.Cjk,
            (false, _, true) => OcrLayoutScript.Latin,
            _ => OcrLayoutScript.Unknown,
        };
    }

    public static bool IsHanIdeograph(char c) =>
        c is >= '一' and <= '鿿' || // CJK Unified Ideographs
        c is >= '㐀' and <= '䶿' || // Extension A
        c is >= '豈' and <= '﫿';   // Compatibility Ideographs

    public static bool IsKana(char c) =>
        c is >= 'ぁ' and <= 'ヿ' || // Hiragana + Katakana
        c is >= 'ㇰ' and <= 'ㇿ';   // Katakana Phonetic Extensions
}
