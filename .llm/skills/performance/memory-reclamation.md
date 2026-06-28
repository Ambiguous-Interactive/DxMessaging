---
title: "DxMessaging Memory Reclamation"
id: "memory-reclamation"
category: "performance"
version: "1.1.0"
created: "2026-05-04"
updated: "2026-05-06"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/MessageBus/IMessageBus.cs"
    - path: "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs"
    - path: "Runtime/Core/Pooling/DxPools.cs"
    - path: "Tests/Runtime/MemoryReclaim/MemoryReclamationTests.cs"
    - path: "Tests/Editor/Contract/MessageBusInvariantTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "memory"
  - "reclamation"
  - "eviction"
  - "pooling"
  - "messaging"

complexity:
  level: "advanced"
  reasoning: "Requires understanding DxMessaging's per-type caches, target/source slots, typed-handler slots, and dispatch snapshot lifetime."

impact:
  performance:
    rating: "critical"
    details: "Keeps long-lived sessions from retaining every message type and InstanceId ever touched while preserving zero-allocation dispatch."
  maintainability:
    rating: "high"
    details: "Central registry guardrails force new message caches to declare their sweep behavior."
  testability:
    rating: "high"
    details: "MemoryReclaim tests, LeakWatcher slot checks, allocation budgets, and reflection contracts pin the behavior."

prerequisites:
  - "cache-eviction-policies"
  - "collection-pooling"
  - "allocation-coverage-required-for-dispatch"

dependencies:
  packages: []
  skills:
    - "cache-eviction-policies"
    - "cache-eviction-builder"
    - "array-pooling"
    - "collection-pooling"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "DxMessaging trim"
  - "MemoryReclaim"
  - "idle eviction"

related:
  - "cache-eviction-policies"
  - "cache-eviction-builder"
  - "array-pooling"
  - "collection-pooling"
  - "memory-reclaim-coverage"

status: "stable"
---

# DxMessaging Memory Reclamation

> **One-line summary**: Empty DxMessaging slots are reclaimed by a
> counter-based idle policy, explicit `Trim`, and shared pool caps without
> adding allocations to dispatch.

## Overview

DxMessaging stores dispatch state by message type, by priority, and, for
targeted and broadcast paths, by `InstanceId`. That shape is required for fast
lookups, but a long-running process can otherwise retain slots for every type or
entity ever touched. Memory reclamation keeps those empty slots bounded.

The runtime uses two reclamation paths. Idle sweeps run from emit calls and the
Unity PlayerLoop when `DxMessagingRuntimeSettings.EvictionEnabled` is true.
Explicit `IMessageBus.Trim(force)` and `MessageHandler.TrimAll(force)` give
tests, scene transitions, and maintenance windows a synchronous reclaim point.

Only empty slots are reset. Active registrations are never evicted because a
long-lived listener is valid game state, not stale cache state.

## Solution

Treat memory reclamation as an owned registry problem. Every cache that can hold
per-type or per-context state must be inventoried, dirty-tracked, and connected
to either the sweepable bus registry, typed-handler sweep dispatch, or the shared
pool trim path.

## Inventoried Memory Holders

| Holder                  | Key                                               | Reclaimed By                                  |
| ----------------------- | ------------------------------------------------- | --------------------------------------------- |
| Bus scalar sinks        | message type                                      | `MessageBus.SweepableTypeCaches`              |
| Bus context sinks       | message type and `InstanceId`                     | `MessageBus.SweepableTypeCaches`              |
| Interceptor caches      | message type                                      | `MessageBus.SweepableTypeCaches`              |
| Typed handler slots     | message type, handler, priority, optional context | `MessageHandler.ResetEmptyTypedSlotsForSweep` |
| Global accept-all slot  | global handler delegates                          | `MessageBus.SweepGlobalSlot`                  |
| Shared collection pools | pooled dictionaries, lists, stacks, sets          | `DxPools.TrimAll`                             |
| Bus context map pool    | targeted/broadcast context dictionaries           | `MessageBus.Trim` and settings hot reload     |

Any new holder keyed by message type or `InstanceId` must have an explicit row
in tests and, if it is a `MessageCache<>` field, an entry in the sweepable
registry.

## Eviction Policy

The idle policy is counter-based, not wall-clock based. `MessageBus` increments
its tick counter on emit, register, and deregister operations, then stamps
touched slots with that counter. A slot becomes eligible when it is empty and
its touch age exceeds the configured idle threshold.

Wall-clock time controls sweep cadence only. `IDxMessagingClock` decides when
enough seconds have elapsed to run another idle sweep; tests inject `FakeClock`
so cadence checks remain deterministic. Force trims ignore idle age and reclaim
all empty candidates immediately.

Sweep candidates are dirty-tracked. The bus does not scan every possible
message type each frame; it revisits the types, targets, interceptors, and
handlers touched since the previous sweep.

## Pool Layer

`CollectionPool<T>` backs the internal reusable collections. `DxPools`
centralizes the pools for `InstanceId` dictionaries, dirty-target
`List<InstanceId>` and `HashSet<InstanceId>` holders, typed-handler context
dictionaries, typed-handler priority dictionaries, object lists, object stacks,
and integer sets.

`MessageBus` also owns the private static `ContextHandlerByTargetDicts` pool for
bus-side targeted and sourced-broadcast context dictionaries. This pool stays
inside `MessageBus` because its value type references private handler-cache
types. It must be configured with the same runtime settings as `DxPools`,
trimmed from `MessageBus.Trim`, and covered by memory-reclamation tests.

`DxMessagingRuntimeSettings.BufferMaxDistinctEntries` controls the retained
entry cap for each pool. `BufferUseLruEviction` chooses between LRU retention
and bounded LIFO behavior. `DxPools.Configure(settings)` hot-reloads both the
cap and retention mode without recreating buses; bus-owned pools must mirror
the same settings in `MessageBus.ApplyRuntimeSettings`.

## Adding a MessageCache

When adding a new `MessageCache<>` storage field to `MessageBus`:

1. Bump `MessageBus.ExpectedMessageCacheFieldCount`.
1. Add a matching row to `MessageBus.SweepableTypeCaches`.
1. Add or update `MessageBusInvariantTests` coverage for the field.
1. Add a `MemoryReclamationTests` fixture row that proves the new cache trims.
1. Update `LeakWatcher` if the cache introduces a new public leak counter.
1. Keep stale deregistration closures safe after sweep; a stale closure must
   not remove a later registration that reused the same slot.
1. If the cache introduces a dirty-tracking collection or bus-owned pool, add a
   sweep-time compaction or return-to-pool test that proves the object is both
   returned and reused.

## Performance Notes

- Dispatch remains zero-allocation; sweep work is outside the hot handler loop.
- Touching a slot is a single counter write on register, deregister, or emit.
- Forced trim does bounded work: the first force-trim reclaims the dirty
  candidate, and every subsequent force-trim is an idempotent no-op (evicts no
  type/target slots, stable live-slot count). The suite pins this
  DETERMINISTICALLY through
  `AllocationMatrixTests.RepeatedForcedTrimIsIdempotentAfterReclaim` via the
  `IMessageBus.TrimResult` eviction counts -- no `GC.Alloc` probe, so it never
  flakes in the warm editor (it replaced a former count budget that did).
- Active dispatch snapshots are leased so forced trim cannot return arrays that
  are still being iterated.

## See Also

- [Memory Reclaim Coverage](../testing/memory-reclaim-coverage.md)
- [High-Performance Cache with Eviction Policies](./cache-eviction-policies.md)
- [Cache Builder Configuration](./cache-eviction-builder.md)
- [Array Pooling](./array-pooling.md)
- [Collection Pooling](./collection-pooling.md)

## Changelog

| Version | Date       | Changes                                                                  |
| ------- | ---------- | ------------------------------------------------------------------------ |
| 1.1.0   | 2026-05-06 | Documented bus-side context dictionary and dirty-target collection pools |
| 1.0.0   | 2026-05-04 | Initial version                                                          |
