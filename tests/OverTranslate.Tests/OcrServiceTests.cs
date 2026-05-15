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
    public void CjkEngine_UsesLanguageSpecificModels()
    {
        Assert.Equal("korean", CjkOnnxOcrEngine.GetModelKeyForLanguage("KO"));
        Assert.Equal("cjk", CjkOnnxOcrEngine.GetModelKeyForLanguage("JA"));
        Assert.Equal("cjk", CjkOnnxOcrEngine.GetModelKeyForLanguage("ZH-HANT"));
    }

    [Theory]
    [InlineData("ZH-HANT")]
    [InlineData("KO")]
    public async Task CjkEngine_OfficialModelBundles_RunInference(string language)
    {
        using var engine = new CjkOnnxOcrEngine();
        using var bitmap = new Bitmap(160, 60);

        var blocks = await engine.RecognizeAsync(bitmap, language);

        Assert.NotNull(blocks);
    }
}
