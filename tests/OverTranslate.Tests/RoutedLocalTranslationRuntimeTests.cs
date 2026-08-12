using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class RoutedLocalTranslationRuntimeTests
{
    [Fact]
    public async Task Translate_ReusesOneSessionForSameRouteAndReportsActualRoute()
    {
        var factory = new RecordingFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(new LocalModelCatalog(), factory);

        var first = await runtime.TranslateAsync(new(["Hello"], "EN", "ZH-HANT"));
        var second = await runtime.TranslateAsync(new(["Again"], "EN-US", "ZH-TW"));

        Assert.Equal(["local:Hello"], first.Translations);
        Assert.Equal(["local:Again"], second.Translations);
        Assert.Equal("bergamot-en-zh-hant", runtime.LastRouteId);
        Assert.Single(factory.CreatedRoutes);
    }

    [Fact]
    public async Task Translate_PassesEntirePivotRouteToSessionFactory()
    {
        var factory = new RecordingFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(new LocalModelCatalog(), factory);

        var result = await runtime.TranslateAsync(new(["こんにちは"], "JA", "ZH-HANT"));

        var route = Assert.Single(factory.CreatedRoutes);
        Assert.True(route.IsPivot);
        Assert.Equal(["bergamot-ja-en", "bergamot-en-zh-hant"],
            route.Models.Select(model => model.ModelId));
        Assert.Equal("JA", result.DetectedLanguage);
    }

    [Fact]
    public async Task Translate_ConcurrentFirstUseCreatesOnlyOneSession()
    {
        var factory = new RecordingFactory(delayCreation: true);
        await using var runtime = new RoutedLocalTranslationRuntime(new LocalModelCatalog(), factory);

        var calls = Enumerable.Range(0, 8).Select(index => runtime.TranslateAsync(
            new LocalTranslationRequest([$"line {index}"], "EN", "ZH-HANT")));
        await Task.WhenAll(calls);

        Assert.Single(factory.CreatedRoutes);
    }

    [Fact]
    public async Task Translate_FailedSessionIsEvictedSoNextCallCanRetryCleanly()
    {
        var factory = new FailingThenHealthyFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(new LocalModelCatalog(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.TranslateAsync(
            new LocalTranslationRequest(["first"], "EN", "ZH-HANT")));
        var retry = await runtime.TranslateAsync(
            new LocalTranslationRequest(["second"], "EN", "ZH-HANT"));

        Assert.Equal(["local:second"], retry.Translations);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Preload_LoadsBeforeFirstTranslationAndReusesSession()
    {
        var factory = new RecordingFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(new LocalModelCatalog(), factory);

        await runtime.PreloadAsync("EN", "ZH-HANT");
        await runtime.TranslateAsync(new(["Hello"], "EN", "ZH-HANT"));

        Assert.Single(factory.CreatedRoutes);
        Assert.Equal(1, runtime.LoadedSessionCount);
    }

    [Fact]
    public async Task UnloadIdle_RemovesAndDisposesExpiredSession()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var factory = new RecordingFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(
            new LocalModelCatalog(), factory,
            new LocalRuntimeOptions(2, TimeSpan.FromMinutes(10)), clock);
        await runtime.PreloadAsync("EN", "ZH-HANT");

        clock.Advance(TimeSpan.FromMinutes(11));
        var unloaded = await runtime.UnloadIdleAsync();

        Assert.Equal(1, unloaded);
        Assert.Equal(0, runtime.LoadedSessionCount);
        Assert.Equal(1, factory.DisposedSessions);
    }

    [Fact]
    public async Task SessionLimit_EvictsLeastRecentlyUsedInactiveRoute()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var factory = new RecordingFactory();
        await using var runtime = new RoutedLocalTranslationRuntime(
            new LocalModelCatalog(), factory,
            new LocalRuntimeOptions(1, TimeSpan.FromHours(1)), clock);
        await runtime.PreloadAsync("EN", "ZH-HANT");

        clock.Advance(TimeSpan.FromSeconds(1));
        await runtime.PreloadAsync("ZH-HANT", "EN");

        Assert.Equal(1, runtime.LoadedSessionCount);
        Assert.Equal(1, factory.DisposedSessions);
        Assert.Equal(2, factory.CreatedRoutes.Count);
    }

    private sealed class RecordingFactory(bool delayCreation = false) : ILocalModelSessionFactory
    {
        private readonly object _gate = new();
        public List<LocalTranslationRoute> CreatedRoutes { get; } = [];

        public async Task<ILocalModelSession> CreateAsync(
            LocalTranslationRoute route,
            CancellationToken cancellationToken = default)
        {
            if (delayCreation) await Task.Delay(20, cancellationToken);
            lock (_gate) CreatedRoutes.Add(route);
            return new RecordingSession(() => DisposedSessions++);
        }

        public int DisposedSessions { get; private set; }
    }

    private sealed class RecordingSession(Action? onDispose = null) : ILocalModelSession
    {
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(text => $"local:{text}").ToArray());

        public ValueTask DisposeAsync()
        {
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingThenHealthyFactory : ILocalModelSessionFactory
    {
        public int CreateCount { get; private set; }

        public Task<ILocalModelSession> CreateAsync(
            LocalTranslationRoute route,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return Task.FromResult<ILocalModelSession>(
                CreateCount == 1 ? new FailingSession() : new RecordingSession());
        }
    }

    private sealed class FailingSession : ILocalModelSession
    {
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("worker failed");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
