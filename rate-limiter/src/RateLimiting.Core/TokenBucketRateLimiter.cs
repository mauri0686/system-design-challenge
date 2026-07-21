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

    public TokenBucketRateLimiter(
        int capacity,
        double tokensPerSecond,
        TimeProvider? timeProvider = null,
        int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _time = timeProvider ?? TimeProvider.System;
        _capacity = capacity;
        _tokensPerSecond = tokensPerSecond;

        // A bucket untouched for capacity/rate seconds is full again, and a full
        // bucket is indistinguishable from a brand-new one — so evicting it is free.
        var timeToFull = TimeSpan.FromSeconds(capacity / tokensPerSecond);
        _buckets = new KeyedStateStore<Bucket>(_time, maxTrackedKeys, timeToFull);
    }

    public RateLimitDecision TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var bucket = _buckets.GetOrAdd(key, () => new Bucket(_capacity, _time.GetTimestamp()));

        // Per-bucket lock: contention exists only between concurrent requests of
        // the *same* client, which is exactly the case we must serialize anyway.
        lock (bucket)
        {
            Refill(bucket);

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return RateLimitDecision.Allowed((long)bucket.Tokens);
            }

            var missingTokens = 1.0 - bucket.Tokens;
            return RateLimitDecision.Rejected(TimeSpan.FromSeconds(missingTokens / _tokensPerSecond));
        }
    }

    private void Refill(Bucket bucket)
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(bucket.LastRefill, now);
        if (elapsed <= TimeSpan.Zero)
            return;

        bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed.TotalSeconds * _tokensPerSecond);
        bucket.LastRefill = now;
    }

    private sealed class Bucket(double tokens, long timestamp) : KeyedStateStore<Bucket>.IExpirable
    {
        public double Tokens = tokens;
        public long LastRefill = timestamp;

        public long LastTouched => LastRefill;
    }
}
