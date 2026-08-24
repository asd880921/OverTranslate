using System.Text;

namespace OverTranslate.Services;

internal static class DictionaryLookupEligibility
{
    private const int MaxCharacters = 64;
    private const int MaxWhitespaceSeparatedTerms = 4;
    private const int MaxCjkCharacters = 16;
    private static readonly char[] SentencePunctuation =
        ['.', '．', '。', '?', '？', '!', '！', ',', '，', ';', '；', '…'];

    public static bool IsEligible(string text)
    {
        var candidate = text.Trim();
        if (candidate.Length is 0 or > MaxCharacters) return false;
        if (candidate.IndexOfAny(['\r', '\n']) >= 0) return false;
        if (candidate.IndexOfAny(SentencePunctuation) >= 0) return false;

        var termCount = candidate.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (termCount > MaxWhitespaceSeparatedTerms) return false;

        var characterCount = 0;
        var containsCjk = false;
        foreach (var rune in candidate.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;

            characterCount++;
            containsCjk |= IsCjk(rune.Value);
        }

        return !containsCjk || characterCount <= MaxCjkCharacters;
    }

    private static bool IsCjk(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x323AF or
            >= 0x3040 and <= 0x30FF or
            >= 0x31F0 and <= 0x31FF or
            >= 0xFF66 and <= 0xFF9D;
}
