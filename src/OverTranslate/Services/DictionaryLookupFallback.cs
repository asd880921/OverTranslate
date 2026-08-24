using OverTranslate.Models;

namespace OverTranslate.Services;

internal static class DictionaryLookupFallback
{
    internal static async Task<DictionaryLookupData?> TryAsync(
        IReadOnlyList<Func<CancellationToken, Task<DictionaryLookupData?>>> attempts,
        CancellationToken cancellationToken = default)
    {
        Exception? lastFailure = null;

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await attempt(cancellationToken);
                if (result?.HasContent == true) return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        if (lastFailure is not null) throw lastFailure;
        return null;
    }
}
