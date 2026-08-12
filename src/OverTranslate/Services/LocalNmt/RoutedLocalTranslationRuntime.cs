using System.Collections.Concurrent;
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

/// <summary>
/// Resolves direct or pivot routes and keeps one reusable session per route. Native process and
/// model-file concerns belong to the injected factory; callers only submit language-tagged text.
/// </summary>
public sealed class RoutedLocalTranslationRuntime(
    LocalModelCatalog catalog,
    ILocalModelSessionFactory sessionFactory) : ILocalTranslationRuntime, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ILocalModelSession>>> _sessions = new();
    private bool _disposed;

    public string? LastRouteId { get; private set; }

    public async Task<LocalTranslationResult> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Texts.Count == 0) return new([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var route = catalog.Resolve(request.SourceLanguage, request.TargetLanguage);
        LastRouteId = route.RouteId;
        var lazySession = _sessions.GetOrAdd(
            route.RouteId,
            _ => new Lazy<Task<ILocalModelSession>>(
                () => sessionFactory.CreateAsync(route, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        ILocalModelSession session;
        try
        {
            session = await lazySession.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<ILocalModelSession>>>(
                route.RouteId, lazySession));
            throw;
        }

        IReadOnlyList<string> translations;
        try
        {
            translations = await session.TranslateAsync(request.Texts, cancellationToken);
        }
        catch
        {
            _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<ILocalModelSession>>>(
                route.RouteId, lazySession));
            await session.DisposeAsync();
            throw;
        }
        if (translations.Count != request.Texts.Count)
            throw new InvalidDataException(
                $"Local route {route.RouteId} returned {translations.Count} results for {request.Texts.Count} texts.");

        return new LocalTranslationResult(translations, route.SourceLanguage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();

        foreach (var lazySession in sessions)
        {
            if (!lazySession.IsValueCreated) continue;
            try
            {
                await (await lazySession.Value).DisposeAsync();
            }
            catch
            {
                // A failed native session has already lost its resources with its worker process.
            }
        }
    }
}
