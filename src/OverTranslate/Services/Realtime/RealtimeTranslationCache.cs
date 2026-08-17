using System.Collections.Concurrent;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// What this session has already translated, keyed by engine, language pair and source text.
/// </summary>
/// <remarks>
/// Two different things need to be true when a session pauses, and only one of them is about
/// forgetting.
///
/// A provider call that began before the pause can finish afterwards, and its answer must reach
/// neither the cache nor the screen: the scene has moved on, and a line from before the pause drawn
/// into it is the mistake the pause was called to avoid. That is what the generation is for — see
/// <see cref="Fence"/> — and every route to the screen checks it.
///
/// What the entries hold, on the other hand, does not go stale. "This source text translates to
/// that" is true whatever the screen is showing now, and the engine and language pair that could
/// change the answer are part of the key rather than something to be invalidated. So the fence moves
/// the generation on without dropping what is remembered, and a session resumed over content that
/// has not changed draws it again without paying for the network a second time.
///
/// The entries are therefore untagged. It is <see cref="Set"/> refusing a stale writer under the
/// write gate — not a generation stamped on each entry — that keeps a late answer out.
/// </remarks>
internal sealed class RealtimeTranslationCache
{
    private readonly ConcurrentDictionary<string, string> _entries = new();
    private readonly object _writeGate = new();
    private int _generation;

    public int Generation => Volatile.Read(ref _generation);

    public int Count => _entries.Count;

    public bool IsCurrent(int generation) => generation == Generation;

    /// <summary>
    /// The remembered translation, if this pass is still the current one. Entries survive a
    /// <see cref="Fence"/>; the pass asking for them may not.
    /// </summary>
    public bool TryGet(string key, int generation, out string value)
    {
        if (IsCurrent(generation) && _entries.TryGetValue(key, out var entry))
        {
            value = entry;
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
            _entries[key] = value;
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
    /// Moves to a new generation, so a pass that was already in flight can neither store its answer
    /// nor publish it. What is already remembered stays readable — see the class remarks.
    /// </summary>
    /// <returns>The new generation.</returns>
    public int Fence()
    {
        lock (_writeGate)
        {
            return Interlocked.Increment(ref _generation);
        }
    }
}
