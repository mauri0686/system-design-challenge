using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

/// <summary>Verifies weighted sliding-window admission, decay, retry, and validation behavior.</summary>
public class SlidingWindowRateLimiterTests
{
    private const string Key = "client-1";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>Verifies that exactly the configured rolling-window limit is admitted.</summary>
    [Fact]
    public void Allows_up_to_limit_within_a_window_then_rejects()
    {
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, new FakeTimeProvider());

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed, $"request {i + 1} should pass");

        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    /// <summary>Verifies that adjacent fixed-window bursts cannot double the rolling budget.</summary>
    [Fact]
    public void Prevents_the_classic_fixed_window_boundary_burst()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        // Establish the first window, then fill it immediately before rollover.
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
        time.Advance(TimeSpan.FromSeconds(59));
        for (var i = 0; i < 9; i++)
            Assert.True(limiter.TryAcquire(Key).IsAllowed);

        time.Advance(TimeSpan.FromSeconds(2));

        // A fixed counter would reset and admit another burst. The sliding
        // estimate is 9.83; including this request would exceed the limit.
        var rejected = limiter.TryAcquire(Key);
        Assert.False(rejected.IsAllowed);

        time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
        Assert.False(limiter.TryAcquire(Key).IsAllowed);
    }

    /// <summary>Verifies linear decay of the previous window's weighted contribution.</summary>
    [Fact]
    public void Previous_window_weight_decays_linearly()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire(Key);

        // 90s later we are halfway through the next window: the 5 old requests
        // weigh 5 * 0.5 = 2.5, so seven whole requests fit under the limit.
        time.Advance(TimeSpan.FromSeconds(90));

        var allowed = 0;
        while (limiter.TryAcquire(Key).IsAllowed)
            allowed++;

        Assert.Equal(7, allowed);
    }

    /// <summary>Verifies that state becomes equivalent to fresh after two idle windows.</summary>
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

    /// <summary>Verifies that waiting the advertised delay makes the retry admissible.</summary>
    [Fact]
    public void Waiting_the_reported_retry_after_makes_the_retry_succeed()
    {
        var time = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(limit: 10, Window, time);

        for (var i = 0; i < 10; i++)
            limiter.TryAcquire(Key);

        time.Advance(TimeSpan.FromSeconds(61));
        var rejected = limiter.TryAcquire(Key);
        Assert.False(rejected.IsAllowed);
        Assert.Equal(5, rejected.RetryAfter.TotalSeconds, precision: 3);

        time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
    }

    /// <summary>Verifies that a full current window waits through rollover and weighted decay.</summary>
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
        // At rollover the full current counter becomes the previous counter;
        // it then needs 1/5 of a window to decay one complete request slot.
        Assert.Equal(72, rejected.RetryAfter.TotalSeconds, precision: 3);

        time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire(Key).IsAllowed);
    }

    /// <summary>Verifies that distinct keys receive independent window budgets.</summary>
    [Fact]
    public void Keys_have_independent_budgets()
    {
        var limiter = new SlidingWindowRateLimiter(limit: 2, Window, new FakeTimeProvider());

        limiter.TryAcquire("client-a");
        limiter.TryAcquire("client-a");
        Assert.False(limiter.TryAcquire("client-a").IsAllowed);

        Assert.True(limiter.TryAcquire("client-b").IsAllowed);
    }

    /// <summary>Verifies that invalid limits and durations fail during construction.</summary>
    [Theory]
    [InlineData(0, 60)]    // limit below 1
    [InlineData(-5, 60)]
    [InlineData(long.MaxValue, 60)] // count must remain exactly representable in weighted math
    [InlineData(10, 0)]    // window must be positive
    [InlineData(10, -30)]
    public void Invalid_configuration_is_rejected_up_front(long limit, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlidingWindowRateLimiter(limit, TimeSpan.FromSeconds(windowSeconds)));
    }

    /// <summary>Verifies that null, empty, and whitespace client keys are rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_key_is_rejected(string? key)
    {
        var limiter = new SlidingWindowRateLimiter(limit: 1, Window);
        Assert.ThrowsAny<ArgumentException>(() => limiter.TryAcquire(key!));
    }
}
