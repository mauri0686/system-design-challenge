namespace RateLimiting.Core;

/// <summary>
/// Decides whether an identified request is allowed to proceed right now.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: a single instance is shared by all
/// concurrent requests of the process.
/// </remarks>
public interface IRateLimiter
{
    /// <summary>
    /// Attempts to consume one request from the budget associated with <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Stable identity used to isolate one client's budget.</param>
    /// <returns>The admission decision, remaining budget, and retry delay when rejected.</returns>
    RateLimitDecision TryAcquire(string key);
}
