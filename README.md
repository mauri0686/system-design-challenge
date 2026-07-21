# System Design Challenge

[![CI](https://github.com/mauri0686/system-design-challenge/actions/workflows/ci.yml/badge.svg)](https://github.com/mauri0686/system-design-challenge/actions/workflows/ci.yml)

Working prototype for a problem from *System Design Interview – An Insider's
Guide* (Alex Xu): **[Rate Limiter](rate-limiter/)** (Chapter 4), implemented
in C# / .NET 8.

Design decisions, trade-offs and AI usage are documented in
[rate-limiter/DESIGN.md](rate-limiter/DESIGN.md).

## Quickstart

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Run all 32 tests
dotnet test rate-limiter/RateLimiter.sln

# Run the demo API (http://localhost:5000)
dotnet run --project rate-limiter/src/RateLimiting.Api
```

Exercise the limiter (default: burst of 5, refill 1 token/s):

```bash
# Burst until throttled — the 6th call returns 429 with Retry-After
for i in $(seq 1 6); do
  curl -s -i http://localhost:5000/api/quotes/AAPL -H "X-Client-Id: demo" | head -n 6
done

# Health endpoint is never rate limited
curl -s http://localhost:5000/health
```

### Docker

```bash
docker build -t rate-limiter ./rate-limiter
docker run -p 8080:8080 rate-limiter
curl -s -i http://localhost:8080/api/quotes/AAPL
```

## Layout

```
rate-limiter/
├── DESIGN.md                       # architecture, trade-offs, AI usage
├── src/
│   ├── RateLimiting.Core/          # algorithms (token bucket, sliding window)
│   └── RateLimiting.Api/           # ASP.NET Core middleware + demo API
└── tests/
    └── RateLimiting.Tests/         # unit, concurrency and integration tests
```
