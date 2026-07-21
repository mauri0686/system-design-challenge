# System Design Challenge

[![CI](https://github.com/mauri0686/system-design-challenge/actions/workflows/ci.yml/badge.svg)](https://github.com/mauri0686/system-design-challenge/actions/workflows/ci.yml)

Working prototype for **Rate Limiter** (Chapter 4 of Alex Xu's *System Design
Interview – An Insider's Guide*), implemented in C# / .NET 8.

The prototype includes:

- token bucket and sliding-window-counter algorithms behind one small interface;
- deterministic monotonic time with no timers or test delays;
- thread-safe per-client state and a strict memory cap under key rotation;
- a minimal ASP.NET Core pipeline with `429` and honest `Retry-After` responses;
- 51 deterministic unit, concurrency, adversarial, and HTTP integration tests;
- 97.61% line coverage and 90.69% branch coverage;
- XML documentation on every named method;
- Sonar static analysis, formatting, vulnerability, and coverage gates;
- reproducible SDK selection, pinned CI actions, metrics, and a Docker image.

Architectural decisions, failure policies, trade-offs, and AI usage are documented
in [rate-limiter/DESIGN.md](rate-limiter/DESIGN.md).

## Quickstart

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Build and run all tests
dotnet test rate-limiter/RateLimiter.sln --configuration Release

# Run the demo API at http://localhost:5000
dotnet run --project rate-limiter/src/RateLimiting.Api
```

Exercise the default token bucket (burst of 5, refill of 1 token/second):

```bash
# The first five requests pass; the sixth returns 429 + Retry-After.
for i in $(seq 1 6); do
  curl -s -i http://localhost:5000/api/quotes/AAPL | head -n 7
done

# Health probes bypass the limiter.
curl -s http://localhost:5000/health
```

Select the alternative sliding-window implementation without rebuilding:

```bash
RateLimit__Algorithm=SlidingWindow \
RateLimit__SlidingWindowLimit=10 \
RateLimit__SlidingWindowSeconds=60 \
dotnet run --project rate-limiter/src/RateLimiting.Api
```

### Client identity boundary

The standalone API safely keys clients by remote IP. `X-Client-Id` is ignored by
default so callers cannot rotate arbitrary identifiers to evade the limit.

An authenticated gateway may set `RateLimit__TrustClientIdHeader=true` only if it
strips any caller-supplied `X-Client-Id` and injects a verified client identity.
Production systems should normally key from an authenticated principal instead.

## Quality gates

The repository treats compiler and Sonar diagnostics as errors. CI also verifies
formatting and requires at least 80% line **and** branch coverage in every module:

```bash
dotnet test rate-limiter/RateLimiter.sln --configuration Release \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=cobertura \
  /p:Threshold=80 \
  /p:ThresholdType=line%2cbranch
```

Current verified result: **51/51 tests**, **97.61% lines**, **90.69% branches**,
with **0 build warnings and 0 build errors**.

### Docker

```bash
docker build -t rate-limiter ./rate-limiter
docker run --rm -p 8080:8080 rate-limiter
curl -s -i http://localhost:8080/api/quotes/AAPL
```

## Layout

```text
rate-limiter/
├── Directory.Build.props           # shared Sonar and quality enforcement
├── DESIGN.md                       # architecture, trade-offs, AI usage
├── src/
│   ├── RateLimiting.Core/          # algorithms and bounded keyed state
│   └── RateLimiting.Api/           # ASP.NET Core middleware + demo API
└── tests/
    └── RateLimiting.Tests/         # unit, adversarial, concurrency, HTTP tests
```
