using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using System.Drawing;
using Xunit;

namespace OverTranslate.Tests;

public class OcrServiceTests
{
    [Theory]
    [InlineData("EN")]
    [InlineData("en")]
    [InlineData("ZH")]
    [InlineData("ZH-HANT")]
    [InlineData("JA")]
    [InlineData("KO")]
    public void SupportedLanguages_AreRecognizedByRouter(string code)
    {
        Assert.True(OcrLanguageRouter.IsSupported(code));
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("FR")]
    [InlineData("AUTO")]
    public void UnsupportedLanguages_AreRejectedByRouter(string code)
    {
        Assert.False(OcrLanguageRouter.IsSupported(code));
    }

    [Fact]
    public void UnsupportedLanguageMessage_ContainsLanguageCode()
    {
        var message = OcrLanguageRouter.GetUnsupportedLanguageMessage("de");
        Assert.Contains("DE", message);
    }

    [Fact]
    public void OnnxEngine_UsesLanguageSpecificModels()
    {
        // EN routes to the PP-OCRv5 general ("cjk") model (handles embedded Chinese in English UI);
        // the dedicated Latin model was removed.
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("EN"));
        Assert.Equal("korean", OnnxOcrEngine.GetModelKeyForLanguage("KO"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("JA"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("ZH-HANT"));
    }

    [Theory]
    // Whole-block lone ideographs are icon misreads on a Latin page -> dropped (empty).
    [InlineData("白", "")]
    [InlineData("品", "")]
    // A lone ideograph glued to the start/end of a Latin word is icon noise -> stripped.
    [InlineData("甲Glossaries", "Glossaries")]
    [InlineData("业spoken terms", "spoken terms")]
    // Real text must be preserved untouched:
    [InlineData("2026年5月8日", "2026年5月8日")]   // date glyphs sit next to digits, not letters
    [InlineData("翻譯這個網頁", "翻譯這個網頁")]     // multi-ideograph run = real Chinese
    [InlineData("免費", "免費")]
    [InlineData("Google Translate", "Google Translate")]
    [InlineData("4.3 (82,985)·免費·參考資源", "4.3 (82,985)·免費·參考資源")]
    public void StripLoneIdeographs_RemovesIconNoiseButKeepsRealText(string input, string expected)
    {
        Assert.Equal(expected, OnnxOcrEngine.StripLoneIdeographs(input));
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("ZH-HANT")]
    [InlineData("KO")]
    public async Task OnnxEngine_OfficialModelBundles_RunInference(string language)
    {
        using var engine = new OnnxOcrEngine();
        using var bitmap = new Bitmap(160, 60);

        var blocks = await engine.RecognizeAsync(bitmap, language);

        Assert.NotNull(blocks);
    }

    [Fact]
    public async Task OnnxEngine_SameTextWithDifferentMargins_RecognizesIdentically()
    {
        // Two captures of the same screen region: pixel-for-pixel identical text, the second
        // framed with a few more pixels of empty margin (264x56 vs 270x60, the text offset by
        // exactly (7, 1)). Nothing about the glyphs differs, so the recognized text must not
        // differ either — before the detector-alignment fix the tighter crop read
        // "Skill(domain-modeling)" as "Skill(Donain-nodeling)".
        using var engine = new OnnxOcrEngine();
        using var tight = LoadFixture("capture-264x56.png");
        using var loose = LoadFixture("capture-270x60.png");

        var tightText = TextOf(await engine.RecognizeAsync(tight, "EN"));
        var looseText = TextOf(await engine.RecognizeAsync(loose, "EN"));

        Assert.Equal(looseText, tightText);
        Assert.Contains("skill(domain-modeling)", tightText);
    }

    private static Bitmap LoadFixture(string name) =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // A leading bullet is trimmed before comparing. The capture starts with a 3px dot, and whether
    // the detector stretches the line's box far enough left to swallow it still depends on where
    // in the frame the dot lands — sensitivity to translation, which sizing the input cannot
    // remove. What the fix under test does guarantee, and what this compares, is that the same
    // glyphs are read as the same characters.
    private static string TextOf(IEnumerable<OcrTextBlock> blocks) =>
        string.Join("\n", blocks.Select(b => b.Text.TrimStart('•', ' ')));

    [Fact]
    public async Task OnnxEngine_SwitchingLanguages_ReloadsActiveModel()
    {
        // Switching model keys disposes the previous runtime and loads the next one, so the
        // same engine instance must keep working across repeated language switches.
        using var engine = new OnnxOcrEngine();
        using var bitmap = new Bitmap(160, 60);

        foreach (var language in new[] { "EN", "KO", "EN", "ZH-HANT", "EN" })
            Assert.NotNull(await engine.RecognizeAsync(bitmap, language));
    }
}
