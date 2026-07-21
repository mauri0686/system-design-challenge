namespace RateLimiting.Core;

/// <summary>
/// Sliding window counter rate limiter: at most <c>limit</c> requests per rolling
/// <c>window</c>, approximated with two adjacent fixed windows (the technique
/// popularized by Cloudflare).
/// </summary>
/// <remarks>
/// <para>
/// The rolling count is estimated as
/// <c>previousCount * overlap + currentCount</c>, where <c>overlap</c> is the
/// fraction of the previous fixed window still covered by the rolling window.
/// This smooths out the classic fixed-window flaw (2x bursts around window
/// boundaries) while keeping O(1) memory per key, unlike a sliding log which
/// stores one timestamp per request.
/// </para>
/// <para>
/// Trade-off: the estimate assumes requests were evenly spread across the
/// previous window, so it can be slightly strict or lenient near boundaries.
/// Cloudflare measured ~0.003% of requests mis-decided at scale — an excellent
/// price for constant memory. See DESIGN.md for the comparison.
/// </para>
/// </remarks>
public sealed class SlidingWindowRateLimiter : IRateLimiter
{
    private const int DefaultMaxTrackedKeys = 100_000;

    private readonly TimeProvider _time;
    private readonly long _limit;
    private readonly long _windowTicks; // window length in TimeProvider timestamp units
    private readonly TimeSpan _window;
    private readonly KeyedStateStore<WindowState> _states;

    public SlidingWindowRateLimiter(
        long limit,
        TimeSpan window,
        TimeProvider? timeProvider = null,
        int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _time = timeProvider ?? TimeProvider.System;
        _limit = limit;
        _window = window;
        _windowTicks = (long)(window.TotalSeconds * _time.TimestampFrequency);

        // After two idle windows both counters are zero, which is exactly the
        // state of a fresh entry — so eviction never changes behavior.
        _states = new KeyedStateStore<WindowState>(_time, maxTrackedKeys, window * 2);
    }

    public RateLimitDecision TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var now = _time.GetTimestamp();
        var state = _states.GetOrAdd(key, () => new WindowState(now));

        lock (state)
        {
            AdvanceWindows(state, now);

            var elapsedFraction = (double)(now - state.WindowStart) / _windowTicks;
            var overlap = 1.0 - elapsedFraction;
            var weighted = state.PreviousCount * overlap + state.CurrentCount;

            if (weighted < _limit)
            {
                state.CurrentCount++;
                state.Touch(now);
                return RateLimitDecision.Allowed(Math.Max(0, (long)(_limit - weighted - 1)));
            }

            state.Touch(now);
            return RateLimitDecision.Rejected(ComputeRetryAfter(state, elapsedFraction));
        }
    }

    /// <summary>Rolls the fixed windows forward if <paramref name="now"/> is beyond the current one.</summary>
    private void AdvanceWindows(WindowState state, long now)
    {
        var elapsedTicks = now - state.WindowStart;
        if (elapsedTicks < _windowTicks)
            return;

        var windowsPassed = elapsedTicks / _windowTicks;

        // If more than one full window passed, the old current count is too old
        // to matter: the rolling window no longer overlaps it.
        state.PreviousCount = windowsPassed == 1 ? state.CurrentCount : 0;
        state.CurrentCount = 0;
        state.WindowStart += windowsPassed * _windowTicks;
    }

    private TimeSpan ComputeRetryAfter(WindowState state, double elapsedFraction)
    {
        var timeLeftInWindow = TimeSpan.FromSeconds(
            (1.0 - elapsedFraction) * _window.TotalSeconds);

        // The current window alone is at the limit: decay of the previous
        // window cannot help, the client must wait for the next rollover.
        if (state.CurrentCount >= _limit || state.PreviousCount == 0)
            return timeLeftInWindow;

        // Otherwise the weighted count decays linearly as the previous window
        // slides out. Solve  prev * (1 - f) + cur < limit  for the window
        // fraction f, then convert the difference into wall-clock time.
        var admittingFraction = 1.0 - (double)(_limit - state.CurrentCount) / state.PreviousCount;
        var wait = TimeSpan.FromSeconds(
            (admittingFraction - elapsedFraction) * _window.TotalSeconds);

        if (wait <= TimeSpan.Zero)
            wait = TimeSpan.FromMilliseconds(1); // boundary case: retry immediately after

        return wait < timeLeftInWindow ? wait : timeLeftInWindow;
    }

    private sealed class WindowState(long windowStart) : KeyedStateStore<WindowState>.IExpirable
    {
        public long WindowStart = windowStart;
        public long PreviousCount;
        public long CurrentCount;
        public long LastTouched { get; private set; } = windowStart;

        public void Touch(long now) => LastTouched = now;
    }
}
