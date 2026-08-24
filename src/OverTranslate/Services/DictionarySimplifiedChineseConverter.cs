using OpenccNetLib;

namespace OverTranslate.Services;

internal static class DictionarySimplifiedChineseConverter
{
    private static readonly Opencc Converter = new(OpenccConfig.Tw2Sp);

    internal static string Convert(string source) =>
        source.Length == 0 ? source : Converter.Convert(source, punctuation: false);
}
