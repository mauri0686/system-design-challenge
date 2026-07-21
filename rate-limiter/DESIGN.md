# Rate Limiter — Design Notes

Problem chosen from *System Design Interview – An Insider's Guide* (Alex Xu),
Chapter 4: **Design a Rate Limiter**. This document explains what I built, the
decisions behind it, the trade-offs I accepted, and how I used AI while
building it.

## Why this problem

I work on backend systems for a living and I'm interviewing at a broker: a
public trading/market-data API is exactly the kind of system that lives or
dies by good rate limiting — it protects matching engines and quote services
from misbehaving clients, keeps costs predictable, and gives well-behaved
clients a clear contract (`429` + `Retry-After`) instead of mysterious
timeouts. It is also small enough to implement *well* — working code,
comprehensive tests, no hand-waving — which is what this challenge asks for.

## Scope

An **in-process** rate limiter library plus a minimal ASP.NET Core API that
uses it. Distributed rate limiting is deliberately out of scope for the
prototype, but the design section below explains exactly how I would take it
there and what changes.

```
┌──────────────┐  trusted ID / IP      ┌───────────────────────┐
│   Client     │ ────────────────────► │  RateLimitingMiddleware│──► 200 + X-RateLimit-Remaining
└──────────────┘                       │  (ASP.NET Core)        │──► 429 + Retry-After
                                       └──────────┬────────────┘
                                                  │ TryAcquire(key)
                                       ┌──────────▼────────────┐
                                       │      IRateLimiter      │
                                       ├────────────────────────┤
                                       │ TokenBucketRateLimiter │  ← default
                                       │ SlidingWindowRateLimiter│ ← alternative
                                       └──────────┬────────────┘
                                       ┌──────────▼────────────┐
                                       │  KeyedStateStore<T>    │  bounded per-key state,
                                       │  (internal)            │  strict cap + idle eviction
                                       └────────────────────────┘
```

## Algorithm selection

| Algorithm              | Memory/key | Burst control | Precision | Verdict |
|------------------------|-----------|---------------|-----------|---------|
| Fixed window counter   | O(1)      | ✗ 2× burst at boundaries | Poor at edges | Rejected: the boundary flaw is disqualifying for a public API |
| Sliding window **log** | O(N) timestamps | ✓ exact | Exact | Rejected: unbounded memory per hot key |
| **Sliding window counter** | O(1) | ✓ (approximation) | Workload-dependent | **Implemented** |
| **Token bucket**       | O(1)      | ✓ tunable burst (capacity) | Exact average rate | **Implemented, default** |
| Leaky bucket           | O(queue)  | ✓ smooths to constant outflow | Exact | Rejected: queueing adds latency; for an HTTP API, fast rejection beats delayed processing |

I implemented **two** algorithms behind one `IRateLimiter` interface: the
token bucket (industry default — it expresses the natural contract "burst up
to N, sustained rate R") and the sliding window counter (an O(1) approximation
of "N per rolling window"). Both are O(1) memory and O(1) time per decision.
Cloudflare reported 0.003% mis-decisions over one workload of 400 million
requests, alongside a 6% average difference between real and estimated rates;
that is useful evidence, not a universal accuracy guarantee.

## Key decisions

**Monotonic time via `TimeProvider.GetTimestamp()`.** Refill math must never
depend on wall-clock time: an NTP correction or DST jump would mint (or
destroy) tokens. `GetTimestamp()` is Stopwatch-based and monotonic. Using
.NET 8's built-in `TimeProvider` (instead of a homemade `IClock`) also gives
tests `FakeTimeProvider` for free — every test in the suite is deterministic,
no `Task.Delay`, no flakiness.

**Lazy refill, no timers.** Buckets refill arithmetically when touched
(`elapsed × rate`, capped at capacity). A background timer per key would be
overengineering: more state, more failure modes, identical behavior.

**Per-key locking.** Each bucket/window is protected by its own `lock`.
Contention only exists between concurrent requests *of the same client*,
which must be serialized anyway to keep the invariant. Lock-free CAS loops
were considered and rejected: measurably relevant only at contention levels a
single client should never produce, at a real cost in reviewability.
`ConcurrentDictionary` handles cross-key concurrency.

**Strict memory cap with explicit overload behavior.** Every keyed limiter has
the same hidden failure mode: one state entry per distinct client key. The
store never tracks more than `maxTrackedKeys`, including under concurrent key
rotation. At the cap it first removes entries idle long enough to be equivalent
to fresh state (`capacity/rate` for a token bucket, two windows for the sliding
counter). If every entry is active, the new key receives transient state and is
therefore fail-open until capacity becomes available. Established keys keep
their budgets and memory remains bounded.

Sweeps are frequency-limited (at most once per idle horizon, capped at once per
minute), preventing an attacker from converting a memory defense into an O(N)
scan on every request. The trade-off is deliberate: under extreme cardinality,
availability and a hard memory bound win over enforcement for previously unseen
keys. In production this event should increment an overload metric.

**Honest `Retry-After`.** Rejections carry the exact wait for the algorithm:
`deficit/rate` for the bucket; for the counter, the admission equation is
`prev·(1−f) + cur + 1 ≤ limit`, including the retrying request. When the current
counter is full, the calculation crosses the rollover and includes enough
decay of the new previous counter. Waiting the reported duration is tested to
succeed without an arbitrary timing cushion.

**Error handling philosophy.** Invalid, non-finite, or unrepresentable
configuration and blank keys fail fast — misconfiguration is a startup bug,
not a runtime condition. Cardinality overload follows the documented fail-open
policy and never grows the state map.

## The API layer

Small on purpose: one middleware + two endpoints (`/api/quotes/{symbol}` as
the protected resource, `/health` outside the limiter — orchestrator probes
must never be throttled). The standalone default keys by remote IP. Reading
`X-Client-Id` requires an explicit trust flag and is safe only behind a gateway
that strips caller input and injects authenticated identity; a production
application should prefer the authenticated principal. Observability uses a
debug-level structured rejection log (avoiding log amplification under attack)
and two `System.Diagnostics.Metrics` counters
(`ratelimit.requests.allowed/rejected`) ready for any OTel exporter.
Algorithm selection, limits, memory cap, and the identity trust flag are plain
appsettings — tunable per environment without a rebuild. Both algorithms are
exercised through the real HTTP pipeline. `Dockerfile` runs as the .NET image's
non-root user; `dotnet run` works too.

## Scaling this to production

What changes when one process becomes N instances behind a load balancer:

1. **State moves to Redis.** Both algorithms translate directly: the bucket
   becomes a hash `{tokens, last_refill}`, the window a pair of counters. The
   check-and-decrement must stay atomic → a short **Lua script** (EVALSHA)
   per decision, exactly the same math this repo tests in-process. Keys get
   TTLs — the same lossless-eviction horizon computed here (`capacity/rate`,
   `2×window`) becomes the TTL value.
2. **Failure policy.** If Redis is unreachable: fail-open with local
   fallback limiting (a coarser in-process limiter as circuit-breaker
   fallback), because taking the whole API down over the limiter is worse
   than briefly over-admitting.
3. **Alternative accepted:** slightly stale local counters synced
   asynchronously (how CDNs do it) trade small over-admission for zero
   added latency; a central Lua script trades one Redis round-trip for
   exactness. For a broker's order-entry API I'd take the Redis hop on the
   write path and async sync for read-only market data.
4. **Placement.** This belongs at the edge/gateway for coarse abuse control
   and *also* in the service for per-plan business limits — they answer
   different questions and both stay O(1).

## Testing strategy

51 tests, all deterministic:

- **Behavioral contracts** per algorithm: burst caps, continuous refill,
  cap-at-capacity after idle, exact `Retry-After` values (asserted to the
  millisecond, then *acted on*: tests advance the fake clock by the reported
  wait and assert the retry succeeds).
- **Boundary and retry adversarial tests**: the incoming request is included in
  the weighted estimate, a second boundary burst is rejected, and waits are
  exercised both during linear decay and across a full-window rollover.
- **Concurrency invariants**: 1,000 parallel attempts against a frozen clock
  must admit exactly `capacity` — never one more. Run for both algorithms,
  plus independent budgets across 50 keys under parallel load.
- **Memory bound**: 1,000 rotating keys in parallel against a cap of 10 leave
  exactly 10 tracked entries; idle eviction and transient overflow are tested
  directly through `InternalsVisibleTo`.
- **Integration through the real pipeline** (`WebApplicationFactory`):
  429 + `Retry-After` + `X-RateLimit-Remaining` headers, per-client
  independence, `/health` exemption, safe fallback for missing/oversized
  identities, runtime algorithm selection, and invalid-configuration startup.

The current Coverlet result is **97.61% line coverage** and **90.69% branch
coverage**. CI enforces at least 80% for both metrics in every module. Shared
build settings also generate XML documentation and promote all compiler,
code-analysis, and SonarAnalyzer.CSharp diagnostics to errors; the verified
Release build completes with zero warnings and zero errors.

## How I used AI

I used Claude Code as a pair programmer for implementation speed, test-case
generation, and review. I chose the scope, algorithms, concurrency model,
identity boundary, and failure policies. AI suggestions were accepted only
after I could explain the invariant and encode a test that could falsify it.

The most useful AI-assisted work was adversarial rather than generative:
checking boundary math, retry behavior across rollover, concurrent admission,
and key-rotation pressure. The final design intentionally uses the standard
library (`TimeProvider`, `ConcurrentDictionary`, `System.Diagnostics.Metrics`)
and keeps the production path to one interface, one store, and one middleware.

## References

- Alex Xu, *System Design Interview – An Insider's Guide*, Ch. 4.
- Cloudflare Engineering, [*How we built rate limiting capable of scaling to
  millions of domains*](https://blog.cloudflare.com/counting-things-a-lot-of-different-things/)
  (sliding-window-counter approximation and measured error).
- John Ousterhout, *A Philosophy of Software Design* (deep modules —
  `KeyedStateStore` hides the entire memory-management concern behind one
  method).
- .NET docs: `TimeProvider`, `Microsoft.Extensions.Time.Testing`.
