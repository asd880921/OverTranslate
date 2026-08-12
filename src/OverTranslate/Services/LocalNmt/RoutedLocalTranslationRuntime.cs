using System.IO;

namespace OverTranslate.Services.LocalNmt;

public interface ILocalModelSession : IAsyncDisposable
{
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

public interface ILocalModelSessionFactory
{
    Task<ILocalModelSession> CreateAsync(
        LocalTranslationRoute route,
        CancellationToken cancellationToken = default);
}

public sealed record LocalRuntimeOptions(
    int MaxLoadedSessions,
    TimeSpan IdleTimeout,
    int MaxConcurrentTranslations = 1)
{
    public static LocalRuntimeOptions Default { get; } = new(2, TimeSpan.FromMinutes(10), 1);
}

/// <summary>
/// Resolves direct or pivot routes and keeps one reusable session per route. Native process and
/// model-file concerns belong to the injected factory; callers only submit language-tagged text.
/// </summary>
public sealed class RoutedLocalTranslationRuntime : ILocalTranslationRuntime, IAsyncDisposable
{
    private readonly LocalModelCatalog _catalog;
    private readonly ILocalModelSessionFactory _sessionFactory;
    private readonly LocalRuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionEntry> _sessions = [];
    private readonly SemaphoreSlim _translationSlots;
    private bool _disposed;

    public RoutedLocalTranslationRuntime(
        LocalModelCatalog catalog,
        ILocalModelSessionFactory sessionFactory,
        LocalRuntimeOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _sessionFactory = sessionFactory;
        _options = options ?? LocalRuntimeOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.MaxLoadedSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one local session must be allowed.");
        if (_options.IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Idle timeout must be positive.");
        if (_options.MaxConcurrentTranslations < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one translation slot must be allowed.");
        _translationSlots = new SemaphoreSlim(
            _options.MaxConcurrentTranslations, _options.MaxConcurrentTranslations);
    }

    public string? LastRouteId { get; private set; }

    public int LoadedSessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    public async Task PreloadAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var route = _catalog.Resolve(sourceLanguage, targetLanguage);
        var entry = await AcquireAsync(route, cancellationToken);
        await ReleaseAsync(entry);
    }

    public async Task<int> UnloadIdleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SessionEntry> evicted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            evicted = RemoveIdleEntries(_timeProvider.GetUtcNow());
        }
        foreach (var entry in evicted) await DisposeEntryAsync(entry);
        return evicted.Count;
    }

    public async Task<LocalTranslationResult> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Texts.Count == 0) return new([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var route = _catalog.Resolve(request.SourceLanguage, request.TargetLanguage);
        LastRouteId = route.RouteId;
        var entry = await AcquireAsync(route, cancellationToken);

        try
        {
            await _translationSlots.WaitAsync(cancellationToken);
            try
            {
                var session = await entry.Session.Value;
                IReadOnlyList<string> translations;
                try
                {
                    translations = await session.TranslateAsync(request.Texts, cancellationToken);
                    if (translations.Count != request.Texts.Count)
                        throw new InvalidDataException(
                            $"Local route {route.RouteId} returned {translations.Count} results for {request.Texts.Count} texts.");
                }
                catch
                {
                    lock (_gate) RemoveEntry(entry);
                    await DisposeEntryAsync(entry);
                    throw;
                }
                return new LocalTranslationResult(translations, route.SourceLanguage);
            }
            finally
            {
                _translationSlots.Release();
            }
        }
        finally
        {
            await ReleaseAsync(entry);
        }
    }

    public async ValueTask DisposeAsync()
    {
        SessionEntry[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var entry in sessions) await DisposeEntryAsync(entry);
        _translationSlots.Dispose();
    }

    private async Task<SessionEntry> AcquireAsync(
        LocalTranslationRoute route,
        CancellationToken cancellationToken)
    {
        List<SessionEntry> idle;
        SessionEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            idle = RemoveIdleEntries(now);
            if (!_sessions.TryGetValue(route.RouteId, out entry!))
            {
                entry = new SessionEntry(
                    route.RouteId,
                    new Lazy<Task<ILocalModelSession>>(
                        () => _sessionFactory.CreateAsync(route, CancellationToken.None),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                    now);
                _sessions.Add(route.RouteId, entry);
            }
            entry.ActiveCalls++;
            entry.LastUsed = now;
        }
        foreach (var expired in idle) await DisposeEntryAsync(expired);

        try
        {
            await entry.Session.Value.WaitAsync(cancellationToken);
            return entry;
        }
        catch
        {
            lock (_gate)
            {
                entry.ActiveCalls--;
                RemoveEntry(entry);
            }
            await DisposeEntryAsync(entry);
            throw;
        }
    }

    private async Task ReleaseAsync(SessionEntry entry)
    {
        List<SessionEntry> evicted = [];
        lock (_gate)
        {
            entry.ActiveCalls--;
            entry.LastUsed = _timeProvider.GetUtcNow();
            while (_sessions.Count > _options.MaxLoadedSessions)
            {
                var candidate = _sessions.Values
                    .Where(item => item.ActiveCalls == 0)
                    .MinBy(item => item.LastUsed);
                if (candidate is null) break;
                RemoveEntry(candidate);
                evicted.Add(candidate);
            }
        }
        foreach (var candidate in evicted) await DisposeEntryAsync(candidate);
    }

    private List<SessionEntry> RemoveIdleEntries(DateTimeOffset now)
    {
        var expired = _sessions.Values.Where(entry =>
            entry.ActiveCalls == 0 && now - entry.LastUsed >= _options.IdleTimeout).ToList();
        foreach (var entry in expired) RemoveEntry(entry);
        return expired;
    }

    private void RemoveEntry(SessionEntry entry)
    {
        if (_sessions.TryGetValue(entry.RouteId, out var current) && ReferenceEquals(current, entry))
            _sessions.Remove(entry.RouteId);
    }

    private static async Task DisposeEntryAsync(SessionEntry entry)
    {
        if (Interlocked.Exchange(ref entry.DisposeStarted, 1) != 0 || !entry.Session.IsValueCreated) return;
        try
        {
            await (await entry.Session.Value).DisposeAsync();
        }
        catch
        {
            // A failed native session has already lost its resources with its worker process.
        }
    }

    private sealed class SessionEntry(
        string routeId,
        Lazy<Task<ILocalModelSession>> session,
        DateTimeOffset lastUsed)
    {
        public string RouteId { get; } = routeId;
        public Lazy<Task<ILocalModelSession>> Session { get; } = session;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
        public int ActiveCalls { get; set; }
        public int DisposeStarted;
    }
}
