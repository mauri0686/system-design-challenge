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
    /// <summary>Creates the trusted-header token-bucket API used by integration tests.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        /// <summary>Applies deterministic token-bucket and identity settings to the test host.</summary>
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimit:BurstCapacity", "2");
            builder.UseSetting("RateLimit:TokensPerSecond", "0.0001");
            builder.UseSetting("RateLimit:TrustClientIdHeader", "true");
        }
    }

    /// <summary>Creates an API host that ignores caller-supplied client identity headers.</summary>
    private sealed class UntrustedHeaderFactory : WebApplicationFactory<Program>
    {
        /// <summary>Configures a host that ignores untrusted caller-supplied identities.</summary>
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimit:BurstCapacity", "2");
            builder.UseSetting("RateLimit:TokensPerSecond", "0.0001");
            builder.UseSetting("RateLimit:TrustClientIdHeader", "false");
        }
    }

    /// <summary>Creates an API host backed by the sliding-window limiter.</summary>
    private sealed class SlidingWindowFactory : WebApplicationFactory<Program>
    {
        /// <summary>Configures a host that selects the sliding-window implementation.</summary>
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimit:Algorithm", "SlidingWindow");
            builder.UseSetting("RateLimit:SlidingWindowLimit", "2");
            builder.UseSetting("RateLimit:SlidingWindowSeconds", "3600");
            builder.UseSetting("RateLimit:TrustClientIdHeader", "true");
        }
    }

    /// <summary>Creates an API host with deliberately invalid startup configuration.</summary>
    private sealed class InvalidAlgorithmFactory : WebApplicationFactory<Program>
    {
        /// <summary>Configures a host with an unsupported limiter algorithm.</summary>
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimit:Algorithm", "Unsupported");
        }
    }

    private readonly Factory _factory;

    /// <summary>Initializes the HTTP pipeline tests with their shared application factory.</summary>
    public ApiRateLimitingTests(Factory factory) => _factory = factory;

    /// <summary>Creates a trusted test client with an isolated limiter identity.</summary>
    private HttpClient CreateClient(string clientId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, clientId);
        return client;
    }

    /// <summary>Sends a request that honors cancellation of the currently running test.</summary>
    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string requestUri) =>
        client.GetAsync(requestUri, TestContext.Current.CancellationToken);

    /// <summary>Verifies the HTTP 429 contract, remaining budget, and retry header.</summary>
    [Fact]
    public async Task Requests_over_the_budget_get_429_with_retry_after()
    {
        var client = CreateClient("integration-a");

        var first = await GetAsync(client, "/api/quotes/AAPL");
        var second = await GetAsync(client, "/api/quotes/AAPL");
        var third = await GetAsync(client, "/api/quotes/AAPL");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("1", first.Headers.GetValues("X-RateLimit-Remaining").Single());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal("0", third.Headers.GetValues("X-RateLimit-Remaining").Single());
    }

    /// <summary>Verifies that trusted client identities receive independent budgets.</summary>
    [Fact]
    public async Task Clients_are_limited_independently()
    {
        var exhausted = CreateClient("integration-b");
        await GetAsync(exhausted, "/api/quotes/GGAL");
        await GetAsync(exhausted, "/api/quotes/GGAL");
        var rejected = await GetAsync(exhausted, "/api/quotes/GGAL");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var fresh = CreateClient("integration-c");
        var allowed = await GetAsync(fresh, "/api/quotes/GGAL");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>Verifies that orchestration health probes bypass rate limiting.</summary>
    [Fact]
    public async Task Health_endpoint_is_never_rate_limited()
    {
        var client = CreateClient("integration-d");

        for (var i = 0; i < 10; i++) // far beyond the capacity of 2
        {
            var response = await GetAsync(client, "/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>Verifies that rotating an untrusted identity header cannot bypass the limit.</summary>
    [Fact]
    public async Task Untrusted_client_id_header_cannot_rotate_around_the_limit()
    {
        using var factory = new UntrustedHeaderFactory();
        using var firstIdentity = factory.CreateClient();
        using var secondIdentity = factory.CreateClient();
        using var thirdIdentity = factory.CreateClient();
        firstIdentity.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, "rotated-a");
        secondIdentity.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, "rotated-b");
        thirdIdentity.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, "rotated-c");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(firstIdentity, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secondIdentity, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await GetAsync(thirdIdentity, "/api/quotes/AAPL")).StatusCode);
    }

    /// <summary>Verifies end-to-end selection of the sliding-window algorithm.</summary>
    [Fact]
    public async Task Sliding_window_can_be_selected_from_configuration()
    {
        using var factory = new SlidingWindowFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RateLimitingMiddleware.ClientIdHeader, "sliding-client");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await GetAsync(client, "/api/quotes/AAPL")).StatusCode);
    }

    /// <summary>Verifies that missing and oversized trusted headers safely fall back to the remote address.</summary>
    [Fact]
    public async Task Invalid_trusted_client_id_falls_back_to_remote_address()
    {
        using var factory = new Factory();
        using var missingIdentity = factory.CreateClient();
        using var oversizedIdentity = factory.CreateClient();
        using var repeatedOversizedIdentity = factory.CreateClient();
        oversizedIdentity.DefaultRequestHeaders.Add(
            RateLimitingMiddleware.ClientIdHeader,
            new string('x', 129));
        repeatedOversizedIdentity.DefaultRequestHeaders.Add(
            RateLimitingMiddleware.ClientIdHeader,
            new string('y', 129));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(missingIdentity, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(oversizedIdentity, "/api/quotes/AAPL")).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await GetAsync(repeatedOversizedIdentity, "/api/quotes/AAPL")).StatusCode);
    }

    /// <summary>Verifies that an unsupported algorithm fails fast during application startup.</summary>
    [Fact]
    public void Invalid_algorithm_fails_during_startup()
    {
        using var factory = new InvalidAlgorithmFactory();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Unknown rate-limit algorithm", exception.Message, StringComparison.Ordinal);
    }
}
