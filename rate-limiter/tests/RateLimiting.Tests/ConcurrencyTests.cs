using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

/// <summary>
/// The hard invariant of any in-process limiter: under concurrent load it must
/// never admit more requests than the configured budget. Time is frozen with a
/// FakeTimeProvider so no refill/decay can mask an over-admission bug.
/// </summary>
public class ConcurrencyTests
{
    /// <summary>Verifies token-bucket capacity under simultaneous requests for one key.</summary>
    [Fact]
    public void TokenBucket_never_admits_more_than_capacity_under_parallel_load()
    {
        const int capacity = 100;
        var limiter = new TokenBucketRateLimiter(capacity, tokensPerSecond: 0.001, new FakeTimeProvider());

        var allowed = 0;
        Parallel.For(0, 1_000, _ =>
        {
            if (limiter.TryAcquire("shared-key").IsAllowed)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(capacity, allowed);
    }

    /// <summary>Verifies sliding-window capacity under simultaneous requests for one key.</summary>
    [Fact]
    public void SlidingWindow_never_admits_more_than_limit_under_parallel_load()
    {
        const int limit = 100;
        var limiter = new SlidingWindowRateLimiter(limit, TimeSpan.FromHours(1), new FakeTimeProvider());

        var allowed = 0;
        Parallel.For(0, 1_000, _ =>
        {
            if (limiter.TryAcquire("shared-key").IsAllowed)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(limit, allowed);
    }

    /// <summary>Verifies full independent budgets during concurrent traffic across many keys.</summary>
    [Fact]
    public void Parallel_traffic_on_distinct_keys_gets_full_independent_budgets()
    {
        const int capacity = 20;
        const int keys = 50;
        var limiter = new TokenBucketRateLimiter(capacity, tokensPerSecond: 0.001, new FakeTimeProvider());
        var allowedPerKey = new ConcurrentDictionary<string, int>();

        Parallel.For(0, keys * capacity * 2, i =>
        {
            var key = $"client-{i % keys}";
            if (limiter.TryAcquire(key).IsAllowed)
                allowedPerKey.AddOrUpdate(key, 1, (_, count) => count + 1);
        });

        Assert.Equal(keys, allowedPerKey.Count);
        Assert.All(allowedPerKey.Values, count => Assert.Equal(capacity, count));
    }
}
