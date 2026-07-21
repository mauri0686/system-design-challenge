using Microsoft.Extensions.Time.Testing;
using RateLimiting.Core;
using Xunit;

namespace RateLimiting.Tests;

/// <summary>
/// The memory-bound invariant lives in the (internal) KeyedStateStore, and is
/// deliberately invisible from the public API — eviction is lossless — so it is
/// tested directly here via InternalsVisibleTo.
/// </summary>
public class KeyedStateStoreTests
{
    private sealed class FakeState(long touched) : KeyedStateStore<FakeState>.IExpirable
    {
        public long LastTouched { get; } = touched;
    }

    [Fact]
    public void Evicts_idle_entries_once_the_key_cap_is_exceeded()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 10, idleTimeout: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
            store.GetOrAdd($"stale-{i}", () => new FakeState(time.GetTimestamp()));

        time.Advance(TimeSpan.FromMinutes(2)); // everything above is now idle

        store.GetOrAdd("fresh", () => new FakeState(time.GetTimestamp()));

        Assert.Equal(1, store.Count); // the 10 idle entries were swept, "fresh" survives
    }

    [Fact]
    public void Never_evicts_entries_that_are_still_active()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 10, idleTimeout: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
            store.GetOrAdd($"active-{i}", () => new FakeState(time.GetTimestamp()));

        time.Advance(TimeSpan.FromSeconds(30)); // below the idle timeout

        store.GetOrAdd("one-more", () => new FakeState(time.GetTimestamp()));

        Assert.Equal(11, store.Count); // sweep ran but found nothing idle
    }

    [Fact]
    public void Returns_the_same_state_instance_for_the_same_key()
    {
        var time = new FakeTimeProvider();
        var store = new KeyedStateStore<FakeState>(time, maxTrackedKeys: 100, idleTimeout: TimeSpan.FromMinutes(1));

        var first = store.GetOrAdd("k", () => new FakeState(time.GetTimestamp()));
        var second = store.GetOrAdd("k", () => new FakeState(time.GetTimestamp()));

        Assert.Same(first, second);
    }
}
