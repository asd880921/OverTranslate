using System.Collections.Concurrent;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Session translations tagged with the generation that produced them.
/// </summary>
/// <remarks>
/// Clearing a concurrent dictionary is not enough to empty it: a provider call that began before the
/// clear can finish afterwards and put its old answer back. Generation-tagged entries make that late
/// write harmless, because only answers from the current generation can be read or published — which
/// is what lets a paused session be sure that nothing from before the pause reaches the screen.
/// </remarks>
internal sealed class RealtimeTranslationCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly object _writeGate = new();
    private int _generation;

    public int Generation => Volatile.Read(ref _generation);

    public int Count => _entries.Count;

    public bool IsCurrent(int generation) => generation == Generation;

    public bool TryGet(string key, int generation, out string value)
    {
        if (IsCurrent(generation) &&
            _entries.TryGetValue(key, out var entry) &&
            entry.Generation == generation &&
            IsCurrent(generation))
        {
            value = entry.Value;
            return true;
        }

        value = "";
        return false;
    }

    public void Set(string key, string value, int generation)
    {
        lock (_writeGate)
        {
            if (!IsCurrent(generation)) return;
            _entries[key] = new Entry(generation, value);
        }
    }

    public void ClearIfOverLimit(int limit, int generation)
    {
        lock (_writeGate)
        {
            if (IsCurrent(generation) && _entries.Count > limit) _entries.Clear();
        }
    }

    /// <summary>
    /// Drops every entry and moves to a new generation, so answers still in flight can neither be
    /// stored nor published.
    /// </summary>
    public int Invalidate()
    {
        lock (_writeGate)
        {
            var generation = Interlocked.Increment(ref _generation);
            _entries.Clear();
            return generation;
        }
    }

    private readonly record struct Entry(int Generation, string Value);
}
