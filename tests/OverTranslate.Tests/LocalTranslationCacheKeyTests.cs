using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class LocalTranslationCacheKeyTests
{
    [Fact]
    public void Create_ContainsRuntimeRouteModelsLanguagesAndNormalizationVersion()
    {
        var route = new LocalModelCatalog().Resolve("JA", "ZH-HANT");

        var key = LocalTranslationCacheKey.Create(route, "こんにちは", "ocr-normalization-v2");

        Assert.Equal("bergamot", key.RuntimeId);
        Assert.Equal(LocalModelCatalog.CatalogVersion, key.CatalogVersion);
        Assert.Equal("bergamot-ja-en+bergamot-en-zh-hant", key.RouteId);
        Assert.Equal("a9bf800679bb+559ab90d723a", key.ModelVersions);
        Assert.Equal("JA", key.SourceLanguage);
        Assert.Equal("ZH-HANT", key.TargetLanguage);
        Assert.Equal("ocr-normalization-v2", key.NormalizationVersion);
        Assert.Equal("こんにちは", key.Text);
    }

    [Fact]
    public void Create_ModelOrNormalizationUpgradeProducesDifferentKey()
    {
        var route = new LocalModelCatalog().Resolve("EN", "ZH-HANT");

        var first = LocalTranslationCacheKey.Create(route, "Hello", "v1");
        var normalizedUpgrade = LocalTranslationCacheKey.Create(route, "Hello", "v2");
        var runtimeUpgrade = LocalTranslationCacheKey.Create(route, "Hello", "v1", "bergamot-v2");

        Assert.NotEqual(first, normalizedUpgrade);
        Assert.NotEqual(first, runtimeUpgrade);
    }
}
