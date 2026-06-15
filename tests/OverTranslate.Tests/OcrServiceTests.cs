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
        Assert.Equal("en", OnnxOcrEngine.GetModelKeyForLanguage("EN"));
        Assert.Equal("korean", OnnxOcrEngine.GetModelKeyForLanguage("KO"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("JA"));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage("ZH-HANT"));
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
