using RateLimiting.Api;
using RateLimiting.Core;

var builder = WebApplication.CreateBuilder(args);

// One limiter instance for the whole process. Limits come from configuration so
// operations can tune them per environment without a rebuild.
var burstCapacity = builder.Configuration.GetValue("RateLimit:BurstCapacity", 5);
var tokensPerSecond = builder.Configuration.GetValue("RateLimit:TokensPerSecond", 1.0);
builder.Services.AddSingleton<IRateLimiter>(
    new TokenBucketRateLimiter(burstCapacity, tokensPerSecond));

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

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in integration tests.
public partial class Program;
