namespace RateLimiting.Core;

/// <summary>
/// Result of a rate-limit check. When the request is rejected, <see cref="RetryAfter"/>
/// tells the caller the earliest time a retry could succeed, so clients can back off
/// instead of hammering the service.
/// </summary>
public readonly record struct RateLimitDecision(bool IsAllowed, TimeSpan RetryAfter, long Remaining)
{
    public static RateLimitDecision Allowed(long remaining) =>
        new(true, TimeSpan.Zero, remaining);

    public static RateLimitDecision Rejected(TimeSpan retryAfter) =>
        new(false, retryAfter, 0);
}
