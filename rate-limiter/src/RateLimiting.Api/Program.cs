using RateLimiting.Api;
using RateLimiting.Core;

var builder = WebApplication.CreateBuilder(args);

// One limiter instance for the whole process. Algorithm and limits come from
// configuration so operations can tune them without a rebuild.
var algorithm = builder.Configuration.GetValue("RateLimit:Algorithm", "TokenBucket")
    ?? throw new InvalidOperationException("RateLimit:Algorithm cannot be null.");
var maxTrackedKeys = builder.Configuration.GetValue("RateLimit:MaxTrackedKeys", 100_000);
var burstCapacity = builder.Configuration.GetValue("RateLimit:BurstCapacity", 5);
var tokensPerSecond = builder.Configuration.GetValue("RateLimit:TokensPerSecond", 1.0);
var slidingWindowLimit = builder.Configuration.GetValue("RateLimit:SlidingWindowLimit", 60L);
var slidingWindowSeconds = builder.Configuration.GetValue("RateLimit:SlidingWindowSeconds", 60.0);

IRateLimiter rateLimiter = algorithm.ToLowerInvariant() switch
{
    "tokenbucket" => new TokenBucketRateLimiter(
        burstCapacity,
        tokensPerSecond,
        maxTrackedKeys: maxTrackedKeys),
    "slidingwindow" => new SlidingWindowRateLimiter(
        slidingWindowLimit,
        TimeSpan.FromSeconds(slidingWindowSeconds),
        maxTrackedKeys: maxTrackedKeys),
    _ => throw new InvalidOperationException(
        $"Unknown rate-limit algorithm '{algorithm}'. Use TokenBucket or SlidingWindow."),
};
builder.Services.AddSingleton(rateLimiter);

var app = builder.Build();

// Health stays outside the limiter: orchestrators must always be able to probe.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/health"),
    limited => limited.UseMiddleware<RateLimitingMiddleware>());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Demo endpoint: a fake market-quote lookup — the kind of endpoint a broker's
// public API actually protects with a rate limiter.
app.MapGet("/api/quotes/{symbol}", (string symbol) => Results.Ok(new
{
    symbol = symbol.ToUpperInvariant(),
    price = Math.Round(100 + Random.Shared.NextDouble() * 50, 2),
    currency = "USD",
    asOf = DateTimeOffset.UtcNow,
}));

await app.RunAsync();

// Exposes the implicit Program class to WebApplicationFactory in integration tests.
/// <summary>Marker for the top-level API entry point used by integration tests.</summary>
public partial class Program
{
    /// <summary>Prevents direct construction while preserving test-host discovery.</summary>
    protected Program()
    {
    }
}
