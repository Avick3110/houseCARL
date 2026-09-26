using System.Collections.Concurrent;

namespace HousecarlCore;

/// <summary>A thread-safe memo that also records how many times each key's value was computed, in the same entry.</summary>
sealed class CountedMemo<TKey, TValue> where TKey : notnull
{
    readonly record struct Entry(TValue Value, int Computations);

    readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    /// <summary>The memoised value for <paramref name="key"/>, computing it when absent; a hit allocates nothing.</summary>
    public TValue GetOrAdd<TState>(TKey key, Func<TKey, TState, TValue> compute, TState state)
    {
        if (_entries.TryGetValue(key, out var hit)) return hit.Value;
        var value = compute(key, state);
        // Every computation is counted, a racing one too; the first value stored is the one every caller gets.
        return _entries.AddOrUpdate(key, static (_, v) => new Entry(v, 1),
            static (_, e, _) => e with { Computations = e.Computations + 1 }, value).Value;
    }

    /// <summary>The same, for a factory that needs nothing beyond the key.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> compute) => GetOrAdd(key, static (k, c) => c(k), compute);

    /// <summary>How many times the value for <paramref name="key"/> has been computed; 0 when it never was.</summary>
    public int ComputationsOf(TKey key) => _entries.TryGetValue(key, out var e) ? e.Computations : 0;

    /// <summary>Drops every value and its count.</summary>
    public void Clear() => _entries.Clear();
}
