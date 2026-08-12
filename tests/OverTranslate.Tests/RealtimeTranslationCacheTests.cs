using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeTranslationCacheTests
{
    [Fact]
    public void InvalidateMakesThePreviousGenerationInvisible()
    {
        var cache = new RealtimeTranslationCache();
        var beforeInvalidate = cache.Generation;
        cache.Set("line", "old translation", beforeInvalidate);

        var afterInvalidate = cache.Invalidate();

        Assert.NotEqual(beforeInvalidate, afterInvalidate);
        Assert.False(cache.TryGet("line", afterInvalidate, out _));
    }

    [Fact]
    public void ProviderAnswerThatReturnsAfterInvalidateCannotRepopulateTheCache()
    {
        var cache = new RealtimeTranslationCache();
        var providerRequestGeneration = cache.Generation;

        var invalidatedGeneration = cache.Invalidate();
        cache.Set("line", "late old translation", providerRequestGeneration);

        Assert.False(cache.TryGet("line", invalidatedGeneration, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CurrentGenerationCanBeCachedAfterInvalidate()
    {
        var cache = new RealtimeTranslationCache();
        var generation = cache.Invalidate();

        cache.Set("line", "new translation", generation);

        Assert.True(cache.TryGet("line", generation, out var value));
        Assert.Equal("new translation", value);
    }

    [Fact]
    public void StaleProviderCannotClearTheCurrentGenerationAtTheSizeLimit()
    {
        var cache = new RealtimeTranslationCache();
        var staleGeneration = cache.Generation;
        var currentGeneration = cache.Invalidate();
        cache.Set("line", "new translation", currentGeneration);

        cache.ClearIfOverLimit(0, staleGeneration);

        Assert.True(cache.TryGet("line", currentGeneration, out var value));
        Assert.Equal("new translation", value);
    }
}
