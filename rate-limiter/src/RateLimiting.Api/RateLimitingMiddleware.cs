using System.Diagnostics.Metrics;
using System.Globalization;
using RateLimiting.Core;

namespace RateLimiting.Api;

/// <summary>
/// Applies the process-wide <see cref="IRateLimiter"/> to every request. Remote IP
/// is the safe default key; a trusted upstream may explicitly enable and populate
/// <c>X-Client-Id</c>. Rejections include 429 and <c>Retry-After</c>.
/// </summary>
/// <param name="next">Next middleware in the request pipeline.</param>
/// <param name="limiter">Process-wide admission policy.</param>
/// <param name="logger">Structured diagnostic logger.</param>
/// <param name="configuration">Configuration controlling the trusted identity boundary.</param>
public sealed class RateLimitingMiddleware(
    RequestDelegate next,
    IRateLimiter limiter,
    ILogger<RateLimitingMiddleware> logger,
    IConfiguration configuration)
{
    /// <summary>Header accepted as the client identity only behind a trusted upstream.</summary>
    public const string ClientIdHeader = "X-Client-Id";
    private const int MaxClientIdLength = 128;

    private static readonly Meter Meter = new("RateLimiting.Api");
    private static readonly Counter<long> AllowedCounter =
        Meter.CreateCounter<long>("ratelimit.requests.allowed");
    private static readonly Counter<long> RejectedCounter =
        Meter.CreateCounter<long>("ratelimit.requests.rejected");
    private readonly bool _trustClientIdHeader =
        configuration.GetValue("RateLimit:TrustClientIdHeader", false);

    /// <summary>Applies the rate limit and either forwards the request or returns HTTP 429.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var clientKey = ResolveClientKey(context);
        var decision = limiter.TryAcquire(clientKey);

        context.Response.Headers["X-RateLimit-Remaining"] =
            decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (!decision.IsAllowed)
        {
            RejectedCounter.Add(1);

            var retryAfterSeconds = Math.Max(1L, (long)Math.Ceiling(decision.RetryAfter.TotalSeconds));
            logger.LogDebug(
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

    /// <summary>Builds the stable limiter key from a trusted header or the remote address.</summary>
    private string ResolveClientKey(HttpContext context)
    {
        if (_trustClientIdHeader)
        {
            var clientId = context.Request.Headers[ClientIdHeader].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(clientId) && clientId.Length <= MaxClientIdLength)
                return $"client:{clientId}";
        }

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
