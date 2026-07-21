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
/// Cloudflare measured ~0.003% of requests mis-decided on one large production
/// workload; accuracy remains traffic-dependent. See DESIGN.md for context.
/// </para>
/// </remarks>
public sealed class SlidingWindowRateLimiter : IRateLimiter
{
    private const int DefaultMaxTrackedKeys = 100_000;
    private const long MaxExactCount = (1L << 53) - 1;

    private readonly TimeProvider _time;
    private readonly long _limit;
    private readonly long _windowTicks; // window length in TimeProvider timestamp units
    private readonly TimeSpan _window;
    private readonly KeyedStateStore<WindowState> _states;

    /// <summary>Initializes a weighted sliding-window limiter.</summary>
    /// <param name="limit">Maximum admitted requests per rolling window.</param>
    /// <param name="window">Duration of the rolling window.</param>
    /// <param name="timeProvider">Optional clock used for deterministic testing.</param>
    /// <param name="maxTrackedKeys">Hard upper bound on retained per-key states.</param>
    public SlidingWindowRateLimiter(
        long limit,
        TimeSpan window,
        TimeProvider? timeProvider = null,
        int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxExactCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            window,
            TimeSpan.FromTicks(TimeSpan.MaxValue.Ticks / 2));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _time = timeProvider ?? TimeProvider.System;
        _limit = limit;
        _window = window;

        var timestampTicks = window.TotalSeconds * _time.TimestampFrequency;
        if (!double.IsFinite(timestampTicks) || timestampTicks < 1 || timestampTicks >= long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(window), "Window is outside the time provider's supported range.");

        _windowTicks = (long)Math.Ceiling(timestampTicks);

        // After two idle windows both counters are zero, which is exactly the
        // state of a fresh entry — so eviction never changes behavior.
        _states = new KeyedStateStore<WindowState>(_time, maxTrackedKeys, window * 2);
    }

    /// <inheritdoc />
    public RateLimitDecision TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _states.Use(
            key,
            () => new WindowState(_time.GetTimestamp()),
            state =>
        {
            var now = _time.GetTimestamp();
            AdvanceWindows(state, now);

            var elapsedFraction = (double)(now - state.WindowStart) / _windowTicks;
            var overlap = 1.0 - elapsedFraction;
            var weighted = state.PreviousCount * overlap + state.CurrentCount;
            var weightedAfterRequest = weighted + 1;

            if (weightedAfterRequest <= _limit)
            {
                state.CurrentCount++;
                state.Touch(now);
                var remaining = (long)Math.Floor(_limit - weightedAfterRequest);
                return RateLimitDecision.Allowed(Math.Max(0, remaining));
            }

            state.Touch(now);
            return RateLimitDecision.Rejected(ComputeRetryAfter(state, elapsedFraction));
        });
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

    /// <summary>Computes a rounded-up delay that makes the next request admissible.</summary>
    private TimeSpan ComputeRetryAfter(WindowState state, double elapsedFraction)
    {
        double waitInWindows;

        if (state.CurrentCount < _limit && state.PreviousCount > 0)
        {
            // Solve prev * (1 - f) + cur + 1 <= limit for f. The +1 is the
            // retrying request itself; omitting it would over-admit by one.
            var allowedPreviousWeight = _limit - state.CurrentCount - 1;
            var admittingFraction = 1.0 - (double)allowedPreviousWeight / state.PreviousCount;
            waitInWindows = Math.Max(0, admittingFraction - elapsedFraction);
        }
        else
        {
            // The current counter is full. At rollover it becomes the previous
            // counter, so merely waiting for rollover is not enough: it must
            // also decay far enough to make room for the retrying request.
            var untilRollover = 1.0 - elapsedFraction;
            var fractionAfterRollover = state.CurrentCount == 0
                ? 0
                : 1.0 - (double)(_limit - 1) / state.CurrentCount;
            waitInWindows = untilRollover + Math.Clamp(fractionAfterRollover, 0, 1);
        }

        // Round upward to a TimeSpan tick so advancing by the advertised value
        // can never land infinitesimally before the admission boundary.
        var waitTicks = decimal.Ceiling((decimal)waitInWindows * _window.Ticks);
        return TimeSpan.FromTicks(Math.Max(1, (long)waitTicks));
    }

    /// <summary>Stores the two counters and timestamps required for one client's rolling estimate.</summary>
    /// <param name="windowStart">Monotonic timestamp at which the current fixed window began.</param>
    private sealed class WindowState(long windowStart) : KeyedStateStore<WindowState>.IExpirable
    {
        private long _lastTouched = windowStart;

        public long WindowStart = windowStart;
        public long PreviousCount;
        public long CurrentCount;
        public long LastTouched => Volatile.Read(ref _lastTouched);

        /// <summary>Records the timestamp of the window state's most recent access.</summary>
        public void Touch(long now) => Volatile.Write(ref _lastTouched, now);
    }
}
