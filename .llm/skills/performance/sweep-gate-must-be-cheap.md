---
title: "DxMessaging Sweep Gate Must Be Cheap"
id: "sweep-gate-must-be-cheap"
category: "performance"
version: "1.0.0"
created: "2026-05-05"
updated: "2026-05-05"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/Pooling/StopwatchClock.cs"
    - path: "Runtime/Core/Pooling/IDxMessagingClock.cs"
    - path: "Tests/Editor/Allocations/EmitGateClockReadIsRare.cs"
    - path: "Tests/Editor/Contract/EvictionSweepContractTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "sweep"
  - "eviction"
  - "clock"
  - "hot-path"
  - "messaging"

complexity:
  level: "advanced"
  reasoning: "Requires understanding the relationship between emission cadence, wall-clock idle eviction, and platform-specific Stopwatch costs."

impact:
  performance:
    rating: "critical"
    details: "An unconditional clock read per emission caused a measured ~30-50% throughput regression in the GC landing."
  maintainability:
    rating: "high"
    details: "A small, reviewable rule set for one of the highest-leverage perf surfaces."
  testability:
    rating: "high"
    details: "EmitGateClockReadIsRare and EvictionSweepContractTests pin both the gate cadence and the wall-clock semantics."

prerequisites:
  - "memory-reclamation"
  - "dispatch-hot-path"

dependencies:
  packages: []
  skills:
    - "memory-reclamation"
    - "dispatch-hot-path"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.1"

aliases:
  - "TrySweepIdle"
  - "sweep cadence gate"
  - "SweepGateMask"

related:
  - "memory-reclamation"
  - "dispatch-hot-path"
  - "memory-reclaim-coverage"

status: "stable"
---

# DxMessaging Sweep Gate Must Be Cheap

> **One-line summary**: The idle-eviction sweep gate is on the per-emit
> hot path; it must NEVER do an unconditional clock read, virtual call,
> allocation, or syscall. Sample wall-clock at most once per
> `SweepGateMask + 1` emissions.

## Overview

The idle-eviction sweep is rare (cadence-gated to ~5 seconds of wall time
by default), but the gate that decides whether to run it is consulted on
every emission. The "first pass garbage collection" landing wired the gate
to call `IDxMessagingClock.NowSeconds` unconditionally; that translated to
a per-emit `Stopwatch.GetTimestamp()` syscall (~15-20ns on x64, ~60-80ns on
ARM Mono) and accounted for most of a measured ~30-50% throughput
regression. This skill documents the constraints any future gate redesign
must obey.

## The mask-gate pattern

The current gate uses a power-of-two mask to skip the clock read on most
emissions:

```csharp
// _emissionId is the per-emit counter; the gate samples the next id so the
// read lands once per SweepGateMask + 1 emissions. The production gate writes
// the inverse (early-return when the masked value is non-zero).
if ((unchecked(_emissionId + 1) & SweepGateMask) == 0)
{
    double nowSeconds = _clock.NowSeconds;
    if (nowSeconds - _lastSweepSeconds >= _evictionTickIntervalSeconds)
    {
        // ... perform sweep
    }
}
```

`SweepGateMask` is `0x0F` by default (sample once per 16 emissions).
Tunable internally; not exposed as public API. The wall-clock comparison
preserves `_evictionTickIntervalSeconds` semantics -- the configured "30
seconds idle then evict" still holds because the comparison still happens,
just less frequently.

## Required properties of the gate

1. **Sample-not-call.** Never read the clock unconditionally per emission.
1. **Wall-clock semantics preserved.** The comparison against
   `_evictionTickIntervalSeconds` must remain -- the public configuration
   surface must continue to mean what it says.
1. **The read is an interface call -- keep it rare, not free.** `_clock` is an
   `IDxMessagingClock` field (constructor-injected; the default is the sealed
   `StopwatchClock` whose `NowSeconds` is `[MethodImpl(AggressiveInlining)]`).
   Unity Mono does not reliably devirtualize the interface call, so the gate's
   value is the 1-in-16 sampling rate (property 1), not an inlined-away getter.
   Do not redesign the gate on the assumption the read is free.

## Headless / non-Unity host guidance

The PlayerLoop sweep hook is `#if UNITY_2021_3_OR_NEWER` only. In headless
test rigs, dedicated game-server builds without PlayerLoop, or any
non-Unity consumer:

- The mask gate alone may not trip often enough on low-emission workloads.
  At 1 emit/sec with mask `0x0F`, the gate samples once every ~16 seconds --
  about 3x the default `_evictionTickIntervalSeconds`.
- These hosts MUST call `bus.Trim()` periodically themselves (e.g. once
  per frame in a game-server tick loop, or via a custom timer).

## Test injection

Tests inject a probe clock via `MessageBus.CreateForInternalUse(probeClock,
...)`. **Do NOT** use `DxMessagingRuntimeSettingsProvider.Override` to inject
the clock -- the settings asset does not carry a clock; the clock is
constructor-injected.

## Per-emit clock-read budget

When idle eviction is enabled, the gate reads the clock at most **once per
`SweepGateSampleSize` (16) emissions**: the mask gate
(`(unchecked(_emissionId + 1)) & SweepGateMask`) short-circuits the other 15 of
every 16 emits before any `_clock.NowSeconds` read. So the steady-state
clock-read rate is at or below 6.25% of emits, and exactly 0% when idle eviction
is disabled. The read the 1-in-16 sampling does reach is a single
`_clock.NowSeconds` fetch through the constructor-injected `IDxMessagingClock`
(default `StopwatchClock`, sealed); it allocates nothing. The budget is that
1-in-16 sampling rate -- keeping the read rare -- not an assumption that the
per-read interface call is itself free.

`EmitGateClockReadIsRare` asserts the per-emission clock-read count stays at or
below `ceil(emitCount / SweepGateSampleSize) + 1` -- 626 over the 10k-emit run it
exercises. A PR that increases the gate's clock-read rate will surface in the
dispatch throughput numbers CI regenerates per PR (`perf-numbers.yml`); justify
any regression in the PR description.

## Enforcement

- `Tests/Editor/Allocations/EmitGateClockReadIsRare.cs` -- pins the gate
  cadence; runs in default CI (lifted from `[Explicit]` after T1.1 ships).
- `Tests/Editor/Contract/EvictionSweepContractTests.cs` -- pins the
  wall-clock idle-eviction semantics. Touching the gate must keep these
  tests green.
- The hot-path file list in `dispatch-hot-path` includes
  `MessageBus.cs:TrySweepIdle`; PR-template enforcement applies.

## Common pitfalls

- "I'll just use `Time.realtimeSinceStartup` instead of `Stopwatch`." Both
  are syscalls/property gets that cost ~10-20ns; the cost is the call
  itself, not the API. Sample-not-call still required.
- "I'll bump the mask to 0x3F so the clock read is even rarer." Higher
  masks bound the worst-case skew between configured cadence and observed
  cadence. At 0x3F, a 1-emit/sec workload would skew ~64 seconds -- much
  larger than the 5-second default. Don't increase past 0x0F without
  measurement.
- "I'll add a per-emit `_tickCounter++` increment for diagnostics." That
  field already exists and is incremented inside `AdvanceTick`; do NOT add
  duplicate increments. Giving the sweep gate its own counter rather than
  reusing the per-emit `_emissionId` was considered and rejected (it broke
  `CounterBasedTouchTests`).

## See also

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [DxMessaging Memory Reclamation](./memory-reclamation.md)
- [Memory Reclaim Coverage](../testing/memory-reclaim-coverage.md)
