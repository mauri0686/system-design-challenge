using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

/// <summary>
/// The memory-bound invariant lives in the internal KeyedStateStore, so strict
/// capacity, lossless idle eviction, and fail-open overflow are tested directly
/// through InternalsVisibleTo.
/// </summary>
public class KeyedStateStoreTests
{
    /// <summary>Minimal expirable state used to exercise the bounded store.</summary>
    /// <param name="touched">Monotonic last-access timestamp.</param>
    private sealed class FakeState(long touched) : KeyedStateStore<FakeState>.IExpirable
    {
        public long LastTouched { get; } = touched;
    }

    /// <summary>Verifies that expired entries are evicted before a fresh key is retained.</summary>
    [Fact]
    public void Evicts_idle_entries_once_the_key_cap_is_exceeded()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 10, idleTimeout: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
            store.Use($"stale-{i}", () => new FakeState(time.GetTimestamp()), state => state);

        time.Advance(TimeSpan.FromMinutes(2)); // everything above is now idle

        store.Use("fresh", () => new FakeState(time.GetTimestamp()), state => state);

        Assert.Equal(1, store.Count); // the 10 idle entries were swept, "fresh" survives
    }

    /// <summary>Verifies fail-open transient state when all retained entries remain active.</summary>
    [Fact]
    public void At_capacity_keeps_active_entries_and_returns_transient_state_for_new_keys()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 10, idleTimeout: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
            store.Use($"active-{i}", () => new FakeState(time.GetTimestamp()), state => state);

        time.Advance(TimeSpan.FromSeconds(30)); // below the idle timeout

        var firstTransient = store.Use(
            "one-more",
            () => new FakeState(time.GetTimestamp()),
            state => state);
        var secondTransient = store.Use(
            "one-more",
            () => new FakeState(time.GetTimestamp()),
            state => state);

        Assert.Equal(10, store.Count);
        Assert.NotSame(firstTransient, secondTransient);
    }

    /// <summary>Verifies that concurrent key rotation never exceeds the hard capacity.</summary>
    [Fact]
    public void Never_exceeds_the_key_cap_under_parallel_rotation()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 10, idleTimeout: TimeSpan.FromMinutes(1));

        Parallel.For(0, 1_000, i =>
            store.Use(
                $"rotating-{i}",
                () => new FakeState(time.GetTimestamp()),
                state => state));

        Assert.Equal(10, store.Count);
    }

    /// <summary>Verifies that repeated access to a retained key returns the same instance.</summary>
    [Fact]
    public void Returns_the_same_state_instance_for_the_same_key()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 100, idleTimeout: TimeSpan.FromMinutes(1));

        var first = store.Use("k", () => new FakeState(time.GetTimestamp()), state => state);
        var second = store.Use("k", () => new FakeState(time.GetTimestamp()), state => state);

        Assert.Same(first, second);
    }

    /// <summary>Verifies that invalid capacity and idle-timeout values fail during construction.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Invalid_configuration_is_rejected(int maxTrackedKeys, int idleSeconds)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new KeyedStateStore<FakeState>(
                new FakeTimeProvider(),
                maxTrackedKeys,
                TimeSpan.FromSeconds(idleSeconds)));
    }
}
