namespace RateLimiting.Core;

/// <summary>
/// Token bucket rate limiter: each key owns a bucket of <c>capacity</c> tokens
/// refilled continuously at <c>tokensPerSecond</c>. A request consumes one token;
/// with an empty bucket it is rejected with an exact retry hint.
/// </summary>
/// <remarks>
/// <para>
/// Chosen as the primary algorithm because it allows short bursts (up to
/// <c>capacity</c>) while enforcing a strict average rate — the usual contract
/// of a public trading/market-data API.
/// </para>
/// <para>
/// Refill is computed lazily from elapsed time on each call — no timers, no
/// background work. Time is measured with <see cref="TimeProvider.GetTimestamp"/>,
/// which is monotonic: wall-clock jumps (NTP, DST) can never mint or destroy tokens.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private const int DefaultMaxTrackedKeys = 100_000;

    private readonly TimeProvider _time;
    private readonly double _capacity;
    private readonly double _tokensPerSecond;
    private readonly KeyedStateStore<Bucket> _buckets;

    /// <summary>Initializes a token bucket limiter with the requested burst and refill limits.</summary>
    /// <param name="capacity">Maximum burst size for each key.</param>
    /// <param name="tokensPerSecond">Continuous refill rate for each key.</param>
    /// <param name="timeProvider">Optional clock used for deterministic testing.</param>
    /// <param name="maxTrackedKeys">Hard upper bound on retained per-key states.</param>
    public TokenBucketRateLimiter(
        int capacity,
        double tokensPerSecond,
        TimeProvider? timeProvider = null,
        int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (!double.IsFinite(tokensPerSecond) || tokensPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond), "Rate must be finite and positive.");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _time = timeProvider ?? TimeProvider.System;
        _capacity = capacity;
        _tokensPerSecond = tokensPerSecond;

        // A bucket untouched for capacity/rate seconds is full again, and a full
        // bucket is indistinguishable from a brand-new one — so evicting it is free.
        var timeToFullSeconds = capacity / tokensPerSecond;
        if (timeToFullSeconds >= TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond), "Rate is too small to represent the refill interval.");

        var timeToFull = TimeSpan.FromSeconds(timeToFullSeconds);
        if (timeToFull < TimeSpan.FromTicks(1))
            timeToFull = TimeSpan.FromTicks(1);

        _buckets = new KeyedStateStore<Bucket>(_time, maxTrackedKeys, timeToFull);
    }

    /// <inheritdoc />
    public RateLimitDecision TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _buckets.Use(
            key,
            () => new Bucket(_capacity, _time.GetTimestamp()),
            bucket =>
        {
            var now = _time.GetTimestamp();
            Refill(bucket, now);
            bucket.Touch(now);

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return RateLimitDecision.Allowed((long)bucket.Tokens);
            }

            var missingTokens = 1.0 - bucket.Tokens;
            var retryAfter = TimeSpan.FromSeconds(missingTokens / _tokensPerSecond);
            return RateLimitDecision.Rejected(
                retryAfter < TimeSpan.FromTicks(1) ? TimeSpan.FromTicks(1) : retryAfter);
        });
    }

    /// <summary>Credits tokens accumulated since the bucket's previous observation.</summary>
    private void Refill(Bucket bucket, long now)
    {
        var elapsed = _time.GetElapsedTime(bucket.LastRefill, now);
        if (elapsed <= TimeSpan.Zero)
            return;

        bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed.TotalSeconds * _tokensPerSecond);
        bucket.LastRefill = now;
    }

    /// <summary>Stores the mutable token balance and timestamps for one client.</summary>
    /// <param name="tokens">Initial token balance.</param>
    /// <param name="timestamp">Monotonic creation timestamp.</param>
    private sealed class Bucket(double tokens, long timestamp) : KeyedStateStore<Bucket>.IExpirable
    {
        private long _lastTouched = timestamp;

        public double Tokens = tokens;
        public long LastRefill = timestamp;

        public long LastTouched => Volatile.Read(ref _lastTouched);

        /// <summary>Records the timestamp of the bucket's most recent access.</summary>
        public void Touch(long now) => Volatile.Write(ref _lastTouched, now);
    }
}
