using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeTranslationCacheTests
{
    [Fact]
    public void RefreshMakesThePreviousGenerationInvisible()
    {
        var cache = new RealtimeTranslationCache();
        var beforeRefresh = cache.Generation;
        cache.Set("line", "old translation", beforeRefresh);

        var afterRefresh = cache.Refresh();

        Assert.NotEqual(beforeRefresh, afterRefresh);
        Assert.False(cache.TryGet("line", afterRefresh, out _));
    }

    [Fact]
    public void ProviderAnswerThatReturnsAfterRefreshCannotRepopulateTheCache()
    {
        var cache = new RealtimeTranslationCache();
        var providerRequestGeneration = cache.Generation;

        var refreshedGeneration = cache.Refresh();
        cache.Set("line", "late old translation", providerRequestGeneration);

        Assert.False(cache.TryGet("line", refreshedGeneration, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CurrentGenerationCanBeCachedAfterRefresh()
    {
        var cache = new RealtimeTranslationCache();
        var generation = cache.Refresh();

        cache.Set("line", "new translation", generation);

        Assert.True(cache.TryGet("line", generation, out var value));
        Assert.Equal("new translation", value);
    }

    [Fact]
    public void StaleProviderCannotClearTheCurrentGenerationAtTheSizeLimit()
    {
        var cache = new RealtimeTranslationCache();
        var staleGeneration = cache.Generation;
        var currentGeneration = cache.Refresh();
        cache.Set("line", "new translation", currentGeneration);

        cache.ClearIfOverLimit(0, staleGeneration);

        Assert.True(cache.TryGet("line", currentGeneration, out var value));
        Assert.Equal("new translation", value);
    }
}
