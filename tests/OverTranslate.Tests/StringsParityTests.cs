using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Holds the string dictionaries to each other, so a key added to one is never left out of the
/// other.
/// </summary>
/// <remarks>
/// A missing key does not break the build and does not throw: LocalizationService.Get returns the
/// key itself, so the mistake ships as a label reading "S.Settings.SavePath" on someone's screen.
/// That is exactly the kind of thing nobody notices in the language they don't use, which is what
/// this test is for.
///
/// The files are parsed as XML rather than loaded as ResourceDictionaries — no WPF application
/// object is needed to compare keys, and parsing keeps the failure message pointed at the file.
/// </remarks>
public class StringsParityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Placeholders like {0}, ignoring {} escapes.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static string ResourcesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OverTranslate", "Resources");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find src/OverTranslate/Resources above {AppContext.BaseDirectory}");
    }

    private static Dictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(ResourcesDirectory(), fileName);
        return XDocument.Load(path).Root!
            .Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value);
    }

    public static readonly string ChineseFile = "Strings.zh-Hant.xaml";
    public static readonly string EnglishFile = "Strings.en.xaml";

    [Fact]
    public void Both_dictionaries_define_exactly_the_same_keys()
    {
        var zh = Load(ChineseFile);
        var en = Load(EnglishFile);

        var missingFromEnglish = zh.Keys.Except(en.Keys).OrderBy(k => k).ToList();
        var missingFromChinese = en.Keys.Except(zh.Keys).OrderBy(k => k).ToList();

        Assert.True(
            missingFromEnglish.Count == 0,
            $"Missing from {EnglishFile}: {string.Join(", ", missingFromEnglish)}");
        Assert.True(
            missingFromChinese.Count == 0,
            $"Missing from {ChineseFile}: {string.Join(", ", missingFromChinese)}");
    }

    /// <summary>
    /// A translation that drops or invents a {0} is a FormatException at the moment the message is
    /// shown — which is to say, on the error path, where it replaces a real diagnostic with a crash.
    /// </summary>
    [Fact]
    public void Composite_formats_use_the_same_placeholders_in_both_languages()
    {
        var zh = Load(ChineseFile);
        var en = Load(EnglishFile);

        foreach (var (key, chinese) in zh)
        {
            if (!en.TryGetValue(key, out var english)) continue;

            var inChinese = Placeholder.Matches(chinese).Select(m => m.Groups[1].Value).ToHashSet();
            var inEnglish = Placeholder.Matches(english).Select(m => m.Groups[1].Value).ToHashSet();

            Assert.True(
                inChinese.SetEquals(inEnglish),
                $"{key} uses {{{string.Join(",", inChinese.Order())}}} in {ChineseFile} " +
                $"but {{{string.Join(",", inEnglish.Order())}}} in {EnglishFile}");
        }
    }

    /// <summary>
    /// The OpenAI hint is the only place the interface tells anyone that a local model is an
    /// option, and Ollama is the route it points at. Losing that in one language loses it for
    /// those users entirely.
    /// </summary>
    [Fact]
    public void OpenAi_hint_recommends_Ollama_in_both_languages()
    {
        foreach (var file in new[] { ChineseFile, EnglishFile })
            Assert.Contains("Ollama", Load(file)["S.Provider.OpenAIHint"]);
    }

    /// <summary>
    /// Full-width punctuation is correct in Chinese and wrong in English, and it is the single
    /// easiest thing to carry over when translating by copying a line and editing it in place.
    /// </summary>
    [Fact]
    public void English_strings_use_no_full_width_punctuation()
    {
        var offenders = Load(EnglishFile)
            .Where(kv => kv.Value.Any("，。：；「」（）、？！／".Contains))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Full-width punctuation in {EnglishFile}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }
}
