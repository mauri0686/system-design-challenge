using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using RateLimiting.Api;
using Xunit;

namespace RateLimiting.Tests;

/// <summary>
/// End-to-end behavior through the real HTTP pipeline: the middleware keys
/// clients, emits 429 + Retry-After, and leaves /health unlimited.
/// Refill is configured to be negligible so the tests are deterministic.
/// </summary>
public class ApiRateLimitingTests : IClassFixture<ApiRateLimitingTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimit:BurstCapacity", "2");
            builder.UseSetting("RateLimit:TokensPerSecond", "0.0001");
        }
    }

    private readonly Factory _factory;

    public ApiRateLimitingTests(Factory factory) => _factory = factory;

    private HttpClient CreateClient(string clientId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, clientId);
        return client;
    }

    [Fact]
    public async Task Requests_over_the_budget_get_429_with_retry_after()
    {
        var client = CreateClient("integration-a");

        var first = await client.GetAsync("/api/quotes/AAPL");
        var second = await client.GetAsync("/api/quotes/AAPL");
        var third = await client.GetAsync("/api/quotes/AAPL");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("1", first.Headers.GetValues("X-RateLimit-Remaining").Single());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal("0", third.Headers.GetValues("X-RateLimit-Remaining").Single());
    }

    [Fact]
    public async Task Clients_are_limited_independently()
    {
        var exhausted = CreateClient("integration-b");
        await exhausted.GetAsync("/api/quotes/GGAL");
        await exhausted.GetAsync("/api/quotes/GGAL");
        var rejected = await exhausted.GetAsync("/api/quotes/GGAL");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var fresh = CreateClient("integration-c");
        var allowed = await fresh.GetAsync("/api/quotes/GGAL");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_never_rate_limited()
    {
        var client = CreateClient("integration-d");

        for (var i = 0; i < 10; i++) // far beyond the capacity of 2
        {
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
