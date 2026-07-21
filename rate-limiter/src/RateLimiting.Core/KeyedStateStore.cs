using System.Collections.Concurrent;

namespace RateLimiting.Core;

/// <summary>
/// Bounded map of per-key limiter state. It hides the one memory concern every
/// keyed rate limiter shares: without eviction, the process keeps one entry per
/// distinct client key forever.
/// </summary>
/// <remarks>
/// Eviction is intentionally lossless: callers pass an <c>idleTimeout</c> after
/// which their state is semantically equivalent to a fresh one (a token bucket
/// that has fully refilled, a sliding window that has fully expired), so removing
/// idle entries never changes limiter behavior — it only frees memory.
///
/// The sweep runs inline on the writer that pushes the map over
/// <c>maxTrackedKeys</c> (amortized, single sweeper at a time). If a concurrent
/// request races with the removal of its entry it simply recreates fresh state,
/// which by the invariant above is the same state. Fail-open by design.
/// </remarks>
internal sealed class KeyedStateStore<TState>(TimeProvider time, int maxTrackedKeys, TimeSpan idleTimeout)
    where TState : class, KeyedStateStore<TState>.IExpirable
{
    internal interface IExpirable
    {
        /// <summary>Timestamp (in <see cref="TimeProvider.GetTimestamp"/> units) of the last access.</summary>
        long LastTouched { get; }
    }

    private readonly ConcurrentDictionary<string, TState> _states = new();
    private int _sweeping; // 0 or 1 — allows a single concurrent sweep

    public int Count => _states.Count;

    public TState GetOrAdd(string key, Func<TState> factory)
    {
        var state = _states.GetOrAdd(key, _ => factory());
        if (_states.Count > maxTrackedKeys)
            SweepIdleEntries();
        return state;
    }

    private void SweepIdleEntries()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
            return; // another thread is already sweeping

        try
        {
            var now = time.GetTimestamp();
            foreach (var (key, state) in _states)
            {
                if (time.GetElapsedTime(state.LastTouched, now) >= idleTimeout)
                    _states.TryRemove(key, out _);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }
}
