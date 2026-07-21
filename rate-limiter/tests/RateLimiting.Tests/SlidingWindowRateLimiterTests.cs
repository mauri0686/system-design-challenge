using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

public class SlidingWindowRateLimiterTests
{
    private const string Key = "client-1";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void Allows_up_to_limit_within_a_window_then_rejects()
    {
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, new FakeTimeProvider());

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed, $"request {i + 1} should pass");

        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Prevents_the_classic_fixed_window_boundary_burst()
    {
        // With a fixed 60s window, 10 requests at t=59 plus 10 at t=61 would all
        // pass (20 requests in 2 seconds). The sliding window must not allow that.
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed);

        time.Advance(TimeSpan.FromSeconds(61));

        // Weighted count: 10 * (59/60) ≈ 9.83 → exactly one slot has decayed free.
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Previous_window_weight_decays_linearly()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire(Key);

        // 90s later we are halfway through the next window: the 5 old requests
        // weigh 5 * 0.5 = 2.5, so exactly 8 more fit under the limit of 10.
        time.Advance(TimeSpan.FromSeconds(90));

        var allowed = 0;
        while (limiter.TryAcquire(Key).IsAllowed)
            allowed++;

        Assert.Equal(8, allowed);
    }

    [Fact]
    public void State_fully_expires_after_two_idle_windows()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 10; i++)
            limiter.TryAcquire(Key);

        time.Advance(TimeSpan.FromSeconds(121)); // both windows rolled over

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed, $"request {i + 1} should pass");
    }

    [Fact]
    public void Waiting_the_reported_retry_after_makes_the_retry_succeed()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 10; i++)
            limiter.TryAcquire(Key);

        time.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire(Key); // consumes the one decayed slot

        var rejected = limiter.TryAcquire(Key);
        Assert.False(rejected.IsAllowed);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);

        // RetryAfter is the exact decay boundary; one millisecond past it the
        // weighted count is strictly under the limit again.
        time.Advance(rejected.RetryAfter + TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
    }

    [Fact]
    public void Rejection_when_current_window_is_full_waits_for_rollover()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 5, Window, time);

        time.Advance(TimeSpan.FromSeconds(15)); // burst mid-window
        for (var i = 0; i < 5; i++)
            limiter.TryAcquire(Key);

        var rejected = limiter.TryAcquire(Key);

        Assert.False(rejected.IsAllowed);
        // Window started at first request (t=15s); it rolls over 60s later.
        Assert.Equal(60, rejected.RetryAfter.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Keys_have_independent_budgets()
    {
        var limiter = new SlidingWindowRateLimiter(limit: 2, Window, new FakeTimeProvider());

        limiter.TryAcquire("client-a");
        limiter.TryAcquire("client-a");
        Assert.False(limiter.TryAcquire("client-a").IsAllowed);

        Assert.True(limiter.TryAcquire("client-b").IsAllowed);
    }

    [Theory]
    [InlineData(0, 60)]    // limit below 1
    [InlineData(-5, 60)]
    [InlineData(10, 0)]    // window must be positive
    [InlineData(10, -30)]
    public void Invalid_configuration_is_rejected_up_front(long limit, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlidingWindowRateLimiter(limit, TimeSpan.FromSeconds(windowSeconds)));
    }
}
