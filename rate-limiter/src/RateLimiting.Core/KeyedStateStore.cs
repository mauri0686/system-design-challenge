using System.Collections.Concurrent;

namespace RateLimiting.Core;

/// <summary>
/// Bounded map of per-key limiter state. It hides the one memory concern every
/// keyed rate limiter shares: without eviction, the process keeps one entry per
/// distinct client key forever.
/// </summary>
/// <remarks>
/// Idle eviction is lossless: callers pass an <c>idleTimeout</c> after which
/// state is equivalent to a fresh entry. When the map is full and no idle entry
/// can be removed, new keys receive transient state and therefore fail open;
/// tracked keys keep their budgets and the memory bound remains strict.
///
/// Sweeps run inline only when a new key reaches the cap and are frequency-limited
/// to prevent a key-rotation attack from turning cleanup into a CPU hot path.
/// </remarks>
internal sealed class KeyedStateStore<TState>
    where TState : class, KeyedStateStore<TState>.IExpirable
{
    /// <summary>Exposes the last-access timestamp required for safe idle eviction.</summary>
    internal interface IExpirable
    {
        /// <summary>Timestamp (in <see cref="TimeProvider.GetTimestamp"/> units) of the last access.</summary>
        long LastTouched { get; }
    }

    private readonly ConcurrentDictionary<string, TState> _states = new();
    private readonly object _admissionLock = new();
    private readonly TimeProvider _time;
    private readonly int _maxTrackedKeys;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sweepInterval;
    private long _lastSweep;
    private bool _hasSwept;

    /// <summary>Initializes a bounded store with lossless idle eviction.</summary>
    /// <param name="time">Clock used to measure state age.</param>
    /// <param name="maxTrackedKeys">Maximum number of retained keys.</param>
    /// <param name="idleTimeout">Age after which a state is equivalent to a fresh state.</param>
    public KeyedStateStore(TimeProvider time, int maxTrackedKeys, TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        _time = time;
        _maxTrackedKeys = maxTrackedKeys;
        _idleTimeout = idleTimeout;
        _sweepInterval = idleTimeout < TimeSpan.FromMinutes(1)
            ? idleTimeout
            : TimeSpan.FromMinutes(1);
    }

    public int Count => _states.Count;

    /// <summary>
    /// Executes <paramref name="action"/> against retained or transient state while
    /// preventing concurrent eviction and serializing operations for the same key.
    /// </summary>
    public TResult Use<TResult>(string key, Func<TState> factory, Func<TState, TResult> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);

        while (true)
        {
            if (_states.TryGetValue(key, out var existing))
            {
                lock (existing)
                {
                    // A sweep can remove an idle state after TryGetValue but
                    // before this lock is acquired. Never operate on that stale
                    // instance alongside a newly admitted state for the same key.
                    if (_states.TryGetValue(key, out var current) && ReferenceEquals(existing, current))
                        return action(existing);
                }

                continue;
            }

            TState? transient = null;

            // Only new-key admission is serialized. Existing keys retain the
            // ConcurrentDictionary fast path and contend only with the same key.
            lock (_admissionLock)
            {
                if (_states.ContainsKey(key))
                    continue;

                if (_states.Count >= _maxTrackedKeys)
                {
                    SweepIdleEntriesIfDue();

                    // Preserve the hard memory bound. A new key is allowed through
                    // with transient state until capacity becomes available. This
                    // explicit fail-open policy protects established clients and
                    // prevents key rotation from growing memory without bound.
                    if (_states.Count >= _maxTrackedKeys)
                        transient = factory();
                }

                if (transient is null)
                    _states.TryAdd(key, factory());
            }

            if (transient is not null)
                return action(transient);
        }
    }

    /// <summary>Removes idle entries when the frequency-limited cleanup interval has elapsed.</summary>
    private void SweepIdleEntriesIfDue()
    {
        var now = _time.GetTimestamp();
        if (_hasSwept && _time.GetElapsedTime(_lastSweep, now) < _sweepInterval)
            return;

        foreach (var (key, state) in _states)
        {
            lock (state)
            {
                if (_states.TryGetValue(key, out var current) &&
                    ReferenceEquals(state, current) &&
                    _time.GetElapsedTime(state.LastTouched, now) >= _idleTimeout)
                {
                    ((ICollection<KeyValuePair<string, TState>>)_states)
                        .Remove(new KeyValuePair<string, TState>(key, state));
                }
            }
        }

        _lastSweep = now;
        _hasSwept = true;
    }
}
