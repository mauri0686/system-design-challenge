using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

public class TokenBucketRateLimiterTests
{
    private const string Key = "client-1";

    [Fact]
    public void Allows_burst_up_to_capacity_then_rejects()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 5, tokensPerSecond: 1, new FakeTimeProvider());

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed, $"request {i + 1} should pass");

        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Remaining_counts_down_with_each_request()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 3, tokensPerSecond: 1, new FakeTimeProvider());

        Assert.Equal(2, limiter.TryAcquire(Key).Remaining);
        Assert.Equal(1, limiter.TryAcquire(Key).Remaining);
        Assert.Equal(0, limiter.TryAcquire(Key).Remaining);
    }

    [Fact]
    public void Rejection_reports_exact_time_until_next_token()
    {
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(capacity: 1, tokensPerSecond: 2, time);

        limiter.TryAcquire(Key); // bucket now empty
        var decision = limiter.TryAcquire(Key);

        Assert.False(decision.IsAllowed);
        // 1 missing token at 2 tokens/s = 0.5s.
        Assert.Equal(0.5, decision.RetryAfter.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Refills_continuously_while_time_passes()
    {
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(capacity: 5, tokensPerSecond: 1, time);

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire(Key);

        time.Advance(TimeSpan.FromSeconds(2.5)); // 2.5 tokens back

        Assert.True(limiter.TryAcquire(Key).IsAllowed);
        Assert.True(limiter.TryAcquire(Key).IsAllowed);

        var rejected = limiter.TryAcquire(Key); // 0.5 tokens left: not enough
        Assert.False(rejected.IsAllowed);
        Assert.Equal(0.5, rejected.RetryAfter.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Waiting_the_reported_retry_after_makes_the_retry_succeed()
    {
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(capacity: 2, tokensPerSecond: 0.4, time);

        limiter.TryAcquire(Key);
        limiter.TryAcquire(Key);
        var rejected = limiter.TryAcquire(Key);
        Assert.False(rejected.IsAllowed);

        time.Advance(rejected.RetryAfter);

        Assert.True(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Long_idle_does_not_accumulate_more_than_capacity()
    {
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(capacity: 5, tokensPerSecond: 1, time);

        time.Advance(TimeSpan.FromHours(1)); // refill is capped at capacity

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed);

        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Keys_have_independent_budgets()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 2, tokensPerSecond: 1, new FakeTimeProvider());

        limiter.TryAcquire("client-a");
        limiter.TryAcquire("client-a");
        Assert.False(limiter.TryAcquire("client-a").IsAllowed);

        Assert.True(limiter.TryAcquire("client-b").IsAllowed);
    }

    [Theory]
    [InlineData(0, 1.0)]   // capacity below 1
    [InlineData(-3, 1.0)]
    [InlineData(5, 0.0)]   // rate must be positive
    [InlineData(5, -2.5)]
    public void Invalid_configuration_is_rejected_up_front(int capacity, double tokensPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TokenBucketRateLimiter(capacity, tokensPerSecond));
    }

    [Fact]
    public void Null_key_is_rejected()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 1, tokensPerSecond: 1);
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!));
    }
}
