namespace RateLimiting.Core;

/// <summary>
/// Result of a rate-limit check. When the request is rejected, <see cref="RetryAfter"/>
/// tells the caller the earliest time a retry could succeed, so clients can back off
/// instead of hammering the service.
/// </summary>
/// <param name="IsAllowed">Whether the request may proceed.</param>
/// <param name="RetryAfter">Minimum safe retry delay when rejected.</param>
/// <param name="Remaining">Estimated requests still available immediately.</param>
public readonly record struct RateLimitDecision(bool IsAllowed, TimeSpan RetryAfter, long Remaining)
{
    /// <summary>Creates a successful decision with the estimated remaining request budget.</summary>
    /// <param name="remaining">Estimated requests that can still be admitted immediately.</param>
    /// <returns>An allowed rate-limit decision.</returns>
    public static RateLimitDecision Allowed(long remaining) =>
        new(true, TimeSpan.Zero, remaining);

    /// <summary>Creates a rejected decision with the minimum safe retry delay.</summary>
    /// <param name="retryAfter">Minimum duration the caller should wait before retrying.</param>
    /// <returns>A rejected rate-limit decision.</returns>
    public static RateLimitDecision Rejected(TimeSpan retryAfter) =>
        new(false, retryAfter, 0);
}
