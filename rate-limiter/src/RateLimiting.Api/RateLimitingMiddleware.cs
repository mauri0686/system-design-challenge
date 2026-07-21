using System.Diagnostics.Metrics;
using System.Globalization;
using RateLimiting.Core;

namespace RateLimiting.Api;

/// <summary>
/// Applies the process-wide <see cref="IRateLimiter"/> to every request, keyed by
/// the <c>X-Client-Id</c> header (falling back to the remote IP). Rejections get
/// a 429 with <c>Retry-After</c> so well-behaved clients can back off precisely.
/// </summary>
public sealed class RateLimitingMiddleware(
    RequestDelegate next,
    IRateLimiter limiter,
    ILogger<RateLimitingMiddleware> logger)
{
    public const string ClientIdHeader = "X-Client-Id";

    private static readonly Meter Meter = new("RateLimiting.Api");
    private static readonly Counter<long> AllowedCounter =
        Meter.CreateCounter<long>("ratelimit.requests.allowed");
    private static readonly Counter<long> RejectedCounter =
        Meter.CreateCounter<long>("ratelimit.requests.rejected");

    public async Task InvokeAsync(HttpContext context)
    {
        var clientKey = ResolveClientKey(context);
        var decision = limiter.TryAcquire(clientKey);

        context.Response.Headers["X-RateLimit-Remaining"] =
            decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (!decision.IsAllowed)
        {
            RejectedCounter.Add(1);

            var retryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds);
            logger.LogInformation(
                "Rate limit exceeded for {ClientKey}; retry after {RetryAfterSeconds}s",
                clientKey, retryAfterSeconds);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            await context.Response.WriteAsJsonAsync(new
            {
                error = "rate_limit_exceeded",
                retryAfterSeconds,
            });
            return;
        }

        AllowedCounter.Add(1);
        await next(context);
    }

    private static string ResolveClientKey(HttpContext context) =>
        context.Request.Headers[ClientIdHeader].FirstOrDefault()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
