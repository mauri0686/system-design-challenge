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
┌──────────────┐   X-Client-Id / IP    ┌───────────────────────┐
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
                                       │  (internal)            │  lossless eviction
                                       └────────────────────────┘
```

## Algorithm selection

| Algorithm              | Memory/key | Burst control | Precision | Verdict |
|------------------------|-----------|---------------|-----------|---------|
| Fixed window counter   | O(1)      | ✗ 2× burst at boundaries | Poor at edges | Rejected: the boundary flaw is disqualifying for a public API |
| Sliding window **log** | O(N) timestamps | ✓ exact | Exact | Rejected: unbounded memory per hot key |
| **Sliding window counter** | O(1) | ✓ (approximation) | ~exact (Cloudflare measured ~0.003% error) | **Implemented** |
| **Token bucket**       | O(1)      | ✓ tunable burst (capacity) | Exact average rate | **Implemented, default** |
| Leaky bucket           | O(queue)  | ✓ smooths to constant outflow | Exact | Rejected: queueing adds latency; for an HTTP API, fast rejection beats delayed processing |

I implemented **two** algorithms behind one `IRateLimiter` interface: the
token bucket (industry default — it expresses the natural contract "burst up
to N, sustained rate R") and the sliding window counter (strict "N per rolling
window" semantics). Both are O(1) memory and O(1) time per decision. The
interface earns its existence by having two real implementations, not
speculative ones.

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

**Bounded memory with lossless eviction.** Every keyed limiter has the same
hidden failure mode: one state entry per distinct client key, forever. That's
an OOM waiting for a key-rotation attack. `KeyedStateStore` caps tracked keys
and sweeps entries idle beyond a horizon chosen so that **eviction never
changes behavior**: a token bucket idle for `capacity/rate` seconds is full
again — indistinguishable from a fresh one; a sliding window idle for two
windows carries zero weight. Evicting them is semantically free. The sweep is
amortized (runs inline on the writer that crosses the cap, single sweeper at
a time — no background threads).

*Accepted race:* a sweep can remove an entry a concurrent request just
obtained; that request operates on the orphaned state and the next one
recreates it fresh. Consequence: a client might get one budget slot more,
never less — **fail-open**, the right direction for this failure.

**Honest `Retry-After`.** Rejections carry the exact wait: `deficit/rate` for
the bucket; for the sliding window, the linear-decay equation
`prev·(1−f) + cur < limit` solved for `f` (falling back to the window
rollover when the current window alone is saturated). Clients that respect
the header retry exactly once, when it will succeed.

**Error handling philosophy.** Invalid configuration and null keys fail fast
with guard clauses (`ArgumentOutOfRangeException.ThrowIf*`) — misconfiguration
is a bug, not a runtime condition. Runtime anomalies degrade open, never
closed, and never throw on the request path.

## The API layer

Small on purpose: one middleware + two endpoints (`/api/quotes/{symbol}` as
the protected resource, `/health` outside the limiter — orchestrator probes
must never be throttled). Clients are keyed by `X-Client-Id` (an API key
stand-in), falling back to remote IP. Observability: structured log on every
rejection and two `System.Diagnostics.Metrics` counters
(`ratelimit.requests.allowed/rejected`) ready for any OTel exporter.
Configuration (`RateLimit:BurstCapacity`, `RateLimit:TokensPerSecond`) is
plain appsettings — tunable per environment without a rebuild. `Dockerfile`
included; `dotnet run` works too.

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

32 tests, all deterministic:

- **Behavioral contracts** per algorithm: burst caps, continuous refill,
  cap-at-capacity after idle, exact `Retry-After` values (asserted to the
  millisecond, then *acted on*: tests advance the fake clock by the reported
  wait and assert the retry succeeds).
- **The boundary-burst test**: the scenario where a fixed window admits 20
  requests in 2 seconds is asserted to admit exactly 11 with the sliding
  window — the test encodes *why* the algorithm was chosen.
- **Concurrency invariants**: 1,000 parallel attempts against a frozen clock
  must admit exactly `capacity` — never one more. Run for both algorithms,
  plus independent budgets across 50 keys under parallel load.
- **Memory bound**: the eviction invariant is behaviorally invisible by
  design, so `KeyedStateStore` is tested directly (via `InternalsVisibleTo`).
- **Integration through the real pipeline** (`WebApplicationFactory`):
  429 + `Retry-After` + `X-RateLimit-Remaining` headers, per-client
  independence, `/health` exemption.

## How I used AI

I used Claude Code as a pair programmer, configured with a minimal-code
policy ([ponytail](https://github.com/DietrichGebert/ponytail)) precisely to
counter the "AI slop" this challenge warns about: prefer the standard library
(that's where `TimeProvider` came from), no speculative abstractions, the
smallest diff that works.

The division of labor: I chose the problem, the two algorithms, the
concurrency model and the eviction invariant; the AI accelerated typing,
proposed the initial test matrix, and challenged edge cases (the
retry-after decay equation and the eviction race were sharpened in that
back-and-forth). Every line was reviewed by me, and the tests encode the
invariants I demanded up front — they were written to *falsify* the
implementation, not to bless it. There is no code in this repo I can't
explain or defend.

## References

- Alex Xu, *System Design Interview – An Insider's Guide*, Ch. 4.
- Cloudflare Engineering, *How we built rate limiting capable of scaling to
  millions of domains* (sliding window counter approximation).
- John Ousterhout, *A Philosophy of Software Design* (deep modules —
  `KeyedStateStore` hides the entire memory-management concern behind one
  method).
- .NET docs: `TimeProvider`, `Microsoft.Extensions.Time.Testing`.
