using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.LocalNmt;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class LocalNmtTranslationProviderTests
{
    [Fact]
    public async Task Provider_MapsRuntimeResultsWithoutLosingBlockGeometry()
    {
        var runtime = new StubRuntime(new LocalTranslationResult(["你好", "早安"], "EN"));
        var provider = new LocalNmtTranslationProvider(runtime);
        var firstLines = new[] { new Rect(1, 2, 30, 10) };
        var blocks = new List<OcrTextBlock>
        {
            new("Hello", new Rect(1, 2, 30, 10), firstLines, 9),
            new("Good morning", new Rect(4, 20, 60, 12), SourceGlyphHeight: 11),
        };

        var (translated, detected) = await provider.TranslateAsync(
            blocks, "EN", "ZH-HANT", "ignored");

        Assert.Equal("EN", detected);
        Assert.Equal(["你好", "早安"], translated.Select(block => block.TranslatedText));
        Assert.Equal(blocks.Select(block => block.Bounds), translated.Select(block => block.Bounds));
        Assert.Same(firstLines, translated[0].SourceLineBounds);
        Assert.Equal(11, translated[1].SourceGlyphHeight);
        Assert.Equal("EN", runtime.LastRequest?.SourceLanguage);
        Assert.Equal("ZH-HANT", runtime.LastRequest?.TargetLanguage);
        Assert.Equal(["Hello", "Good morning"], runtime.LastRequest?.Texts);
    }

    [Fact]
    public async Task TranslationService_LocalSelectionUsesOnlyInjectedLocalProvider()
    {
        var runtime = new StubRuntime(new LocalTranslationResult(["本機結果"], "EN"));
        var service = new TranslationService(new LocalNmtTranslationProvider(runtime));
        var blocks = new List<OcrTextBlock> { new("Local result", Rect.Empty) };

        var result = await service.TranslateAsync(
            blocks, "EN", "ZH-HANT", "", engine: TranslationProvider.LocalNmt);

        Assert.Equal("本機結果", Assert.Single(result.Blocks).TranslatedText);
        Assert.False(service.ProviderRequiresApiKey(TranslationProvider.LocalNmt));
        Assert.Equal(1, runtime.CallCount);
    }

    [Fact]
    public async Task Provider_WhenRuntimeReturnsWrongCount_ThrowsDiagnosticError()
    {
        var provider = new LocalNmtTranslationProvider(
            new StubRuntime(new LocalTranslationResult([], "EN")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.TranslateAsync(
            [new OcrTextBlock("Hello", Rect.Empty)], "EN", "ZH-HANT", ""));

        Assert.Contains("1 blocks", error.Message);
    }

    [Fact]
    public async Task Provider_EmptyBatchDoesNotStartRuntime()
    {
        var runtime = new StubRuntime(new LocalTranslationResult([], ""));
        var provider = new LocalNmtTranslationProvider(runtime);

        var result = await provider.TranslateAsync([], "EN", "ZH-HANT", "");

        Assert.Empty(result.Blocks);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public void CacheIdentity_ContainsRuntimeCatalogRouteModelsLanguagesAndNormalization()
    {
        var provider = new LocalNmtTranslationProvider(
            new StubRuntime(new LocalTranslationResult([], "")));

        var identity = provider.GetCacheIdentity("JA", "ZH-HANT", "ocr-v2");

        Assert.Equal(
            "hy-mt2|issue-47-hy-mt2-v1|ja-zh-hant:hy-mt2-1.8b-q4-k-m|" +
            "1cd5208700ac|JA|ZH-HANT|ocr-v2",
            identity);
    }

    [Fact]
    public void TranslationService_CloudCacheIdentityIncludesNormalizationWithoutLocalProvider()
    {
        var service = new TranslationService();

        var identity = service.GetCacheIdentity(
            TranslationProvider.Microsoft, "EN", "ZH-HANT", "ocr-v2");

        Assert.Equal("Microsoft|EN|ZH-HANT|ocr-v2", identity);
    }

    private sealed class StubRuntime(LocalTranslationResult result) : ILocalTranslationRuntime
    {
        public int CallCount { get; private set; }
        public LocalTranslationRequest? LastRequest { get; private set; }

        public Task<LocalTranslationResult> TranslateAsync(
            LocalTranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
