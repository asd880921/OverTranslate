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

        Assert.Equal("hy-mt2", key.RuntimeId);
        Assert.Equal(LocalModelCatalog.CatalogVersion, key.CatalogVersion);
        Assert.Equal("ja-zh-hant:hy-mt2-1.8b-q4-k-m", key.RouteId);
        Assert.Equal("1cd5208700ac", key.ModelVersions);
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
        var runtimeUpgrade = LocalTranslationCacheKey.Create(route, "Hello", "v1", "hy-mt2-v2");

        Assert.NotEqual(first, normalizedUpgrade);
        Assert.NotEqual(first, runtimeUpgrade);
    }
}
