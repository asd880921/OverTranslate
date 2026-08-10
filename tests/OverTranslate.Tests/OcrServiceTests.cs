using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using System.Drawing;
using System.Drawing.Text;
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
    [InlineData("AUTO")]
    [InlineData("auto")]
    public void SupportedLanguages_AreRecognizedByRouter(string code)
    {
        Assert.True(OcrLanguageRouter.IsSupported(code));
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("FR")]
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
        // EN routes to the general ("cjk") model — PP-OCRv6_small_rec, which covers Chinese,
        // English, Japanese and 46 Latin-script languages in one. KO keeps its own model because
        // v6 holds no Hangul.
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("EN"));
        Assert.Equal("korean", OnnxOcrEngine.GetModelKeyForLanguage("KO"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("JA"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("ZH-HANT"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("AUTO"));
    }

    [Theory]
    [InlineData("Hello world", false)]
    [InlineData("甲Glossaries", false)]
    [InlineData("翻譯", true)]
    [InlineData("2026年5月8日", true)]
    [InlineData("日本語", true)]
    [InlineData("カタカナ", true)]
    [InlineData("English 中文", true)]
    public void AutomaticLayout_IsChosenPerRecognizedBlock(string text, bool expectedCjk)
    {
        Assert.Equal(expectedCjk, OnnxOcrEngine.UsesCjkLayoutForText(text));
    }

    [Fact]
    public void AutomaticLayout_NormalizesEachBlockAndKeepsLatinIconFiltering()
    {
        var bounds = new System.Windows.Rect(0, 0, 120, 20);
        var blocks = new List<OcrTextBlock>
        {
            new("Settings", bounds),
            new("設定", bounds),
            new("白", bounds),
        };

        var normalized = OnnxOcrEngine.NormalizeAutomaticBlocks(blocks);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("Settings", normalized[0].Text);
        Assert.NotNull(normalized[0].SourceGlyphHeight);
        Assert.Equal(bounds.Height, normalized[0].Bounds.Height);
        Assert.Equal("設定", normalized[1].Text);
        Assert.Null(normalized[1].SourceGlyphHeight);
        Assert.True(normalized[1].Bounds.Height < bounds.Height);
    }

    [Fact]
    public void AutomaticLayout_UsesOneEffectiveGlyphScaleAcrossScripts()
    {
        var bounds = new System.Windows.Rect(0, 0, 120, 40);
        var normalized = OnnxOcrEngine.NormalizeAutomaticBlocks(
        [
            new("Settings", bounds),
            new("設定設定設定設定", bounds),
        ]);

        var latinGlyphHeight = normalized[0].SourceGlyphHeight;
        var cjkGlyphHeight = normalized[1].Bounds.Height;

        Assert.NotNull(latinGlyphHeight);
        Assert.Equal(cjkGlyphHeight, latinGlyphHeight.Value, precision: 6);
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
    // The measured case: PP-OCRv6 read a subtitle as "That kind of șong" (U+0219) at 0.93
    // confidence and the line came back from the translator as nonsense.
    [InlineData("That kind of șong", "That kind of song")]
    [InlineData("Ｃafé", "Ｃafe")]
    [InlineData("naïve", "naive")]
    [InlineData("Ārigatō", "Arigato")]
    // Nothing to fold — these must come back the same object's worth of text, untouched.
    [InlineData("That kind of song", "That kind of song")]
    [InlineData("翻譯這個網頁", "翻譯這個網頁")]
    [InlineData("2026年5月8日", "2026年5月8日")]
    public void FoldLatinDiacritics_FoldsAccentsAndLeavesEverythingElse(string input, string expected)
    {
        Assert.Equal(expected, OnnxOcrEngine.FoldLatinDiacritics(input));
    }

    [Fact]
    public void FoldLatinDiacritics_LeavesJapaneseVoicedKanaAlone()
    {
        // The reason the fold is restricted to the Latin ranges. Voiced kana decompose the same way
        // an accented letter does — が is か plus a combining mark — so a blanket normalisation
        // would silently turn "がっこう" into "かっこう", a different word entirely.
        Assert.Equal("がっこう", OnnxOcrEngine.FoldLatinDiacritics("がっこう"));
        Assert.Equal("ポケット", OnnxOcrEngine.FoldLatinDiacritics("ポケット"));
        Assert.Equal("月島まりな", OnnxOcrEngine.FoldLatinDiacritics("月島まりな"));
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

        // Capitalised, as it is in the fixture. This asserted a lowercase "skill" until the
        // PP-OCRv6 model landed, which was never what the picture says — the old model misread the
        // capital and the assertion was written around that, quietly making a recognition error
        // part of the contract. Worth remembering when the next model changes it back.
        Assert.Contains("Skill(domain-modeling)", tightText);
    }

    [Fact]
    public async Task OnnxEngine_AutomaticModeRecognizesLatinTextWithGeneralModel()
    {
        using var engine = new OnnxOcrEngine();
        using var bitmap = LoadFixture("capture-264x56.png");

        var text = TextOf(await engine.RecognizeAsync(bitmap, "AUTO"));

        Assert.Contains("Skill(domain-modeling)", text);
    }

    [Fact]
    public async Task OnnxEngine_AutomaticModeRecognizesChineseInterface()
    {
        using var engine = new OnnxOcrEngine();
        using var bitmap = LoadFixture("設定預覽.png");

        var blocks = await engine.RecognizeAsync(bitmap, "AUTO");

        Assert.NotEmpty(blocks);
        Assert.Contains(blocks, block => OnnxOcrEngine.UsesCjkLayoutForText(block.Text));
    }

    [Fact]
    public async Task OnnxEngine_AutomaticModeRecognizesJapaneseText()
    {
        using var bitmap = new Bitmap(420, 90);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Yu Gothic UI", 38, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("日本語テスト", font, Brushes.Black, new PointF(12, 12));
        }

        using var engine = new OnnxOcrEngine();
        var blocks = await engine.RecognizeAsync(bitmap, "AUTO");
        var text = TextOf(blocks);

        Assert.Contains("日本語", text);
        Assert.Contains(blocks, block => OnnxOcrEngine.UsesCjkLayoutForText(block.Text));
    }

    [Fact]
    public async Task OnnxEngine_AutomaticModeMatchesManualEnglishOnMixedInterface()
    {
        using var engine = new OnnxOcrEngine();
        using var bitmap = LoadFixture("翻譯比對圖.png");

        var manual = TextOf(await engine.RecognizeAsync(bitmap, "EN"));
        var automatic = TextOf(await engine.RecognizeAsync(bitmap, "AUTO"));

        Assert.Equal(manual, automatic);
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
