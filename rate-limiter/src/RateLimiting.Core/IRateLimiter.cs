namespace RateLimiting.Core;

/// <summary>
/// Decides whether a request identified by <paramref name="key"/> (a client id,
/// API key, IP address...) is allowed to proceed right now.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: a single instance is shared by all
/// concurrent requests of the process.
/// </remarks>
public interface IRateLimiter
{
    RateLimitDecision TryAcquire(string key);
}
