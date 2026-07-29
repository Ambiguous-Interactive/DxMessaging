---
name: memory-reclamation
description: "How DxMessaging reclaims empty per-type and per-InstanceId slots through counter-based idle sweeps, IMessageBus.Trim, and DxPools caps, plus the test and documentation duties for any new memory holder. Use when adding a MessageCache field, a dictionary or pool keyed by message type or InstanceId, when slot counts or memory grow over a long session, or when changing DxMessagingRuntimeSettings eviction and buffer settings."
metadata:
  category: "performance"
  tags: "memory, reclamation, eviction, pooling, messaging"
---

# DxMessaging Memory Reclamation

The bus stores dispatch state by message type, by priority, and by `InstanceId`.
Without reclamation a long-running process retains a slot for every type and
entity it ever touched. Idle sweeps and explicit trims keep those empty slots
bounded, and only EMPTY slots are ever reset - an active registration is game
state, not stale cache.

## When to use

- Adding a `MessageCache<>` field to `MessageBus`, or any dictionary, list,
  stack, set, or pool keyed by message type or `InstanceId`.
- Investigating memory or `OccupiedTypeSlots`/`OccupiedTargetSlots` growth across
  a long session or a scene-transition loop.
- Changing `DxMessagingRuntimeSettings`, its provider, `IMessageBus.Trim`,
  `MessageHandler.TrimAll`, or anything under `Runtime/Core/Pooling/`.
- Writing a test that asserts slots return to a baseline.

## Rules

### Reclamation model

- Two paths reclaim: idle sweeps driven from emit calls and the Unity PlayerLoop
  when `DxMessagingRuntimeSettings.EvictionEnabled` is true, and the synchronous
  `IMessageBus.Trim(force)` / `MessageHandler.TrimAll(force)`.
- Idle age is COUNTER-based, not wall-clock. `MessageBus` increments a tick
  counter on emit, register, and deregister, and stamps touched slots with it. A
  slot is eligible when it is empty and its touch age exceeds the configured idle
  threshold. Wall-clock (`IDxMessagingClock`) controls sweep CADENCE only; tests
  inject `FakeClock` for determinism.
- Force trim ignores idle age and reclaims every empty candidate immediately.
- Sweeps are dirty-tracked. The bus revisits only the types, targets,
  interceptors, and handlers touched since the previous sweep - never a full
  scan.
- Dispatch stays zero-allocation: sweep work runs outside the handler loop, a
  slot touch is a single counter write, and active dispatch snapshots are leased
  so a forced trim cannot return an array still being iterated.
- Every holder must reach one of the three reclaim paths:
  `MessageBus.SweepableTypeCaches` (bus scalar sinks, context sinks, interceptor
  caches), `MessageHandler.ResetEmptyTypedSlotsForSweep` /
  `MessageBus.SweepGlobalSlot` (handler slots), or `DxPools.TrimAll` (shared
  collection pools).
- `DxPools` centralizes `InstanceId` dictionaries, dirty-target
  `List<InstanceId>`/`HashSet<InstanceId>`, typed-handler context and priority
  dictionaries, object lists, object stacks, and integer sets.
  `MessageBus` additionally owns the private static `ContextHandlerByTargetDicts`
  pool (it references private handler-cache types, so it cannot move to
  `DxPools`); configure it from the same settings in
  `MessageBus.ApplyRuntimeSettings` and trim it from `MessageBus.Trim`.
- `BufferMaxDistinctEntries` caps retained entries per pool;
  `BufferUseLruEviction` selects LRU retention against bounded LIFO.
  `DxPools.Configure(settings)` hot-reloads both without recreating buses, and
  bus-owned pools must mirror the same settings.

### Adding a MessageCache field

1. Bump `MessageBus.ExpectedMessageCacheFieldCount`.
1. Add the matching row to `MessageBus.SweepableTypeCaches`.
1. Extend `MessageBusInvariantTests` for the new field.
1. Add a `MemoryReclamationTests` row proving the cache trims.
1. Update `LeakWatcher` when the cache adds a public leak counter.
1. Prove a stale deregistration closure cannot remove a LATER registration that
   reused the same slot.
1. Add a sweep-time compaction or return-to-pool test when the cache introduces
   dirty tracking or a bus-owned pool - the object must be both returned AND
   reused.

### Test coverage

- Minimum proof for any holder: create it through public register/emit APIs, make
  it empty, assert `OccupiedTypeSlots` or `OccupiedTargetSlots` rose, run
  `Trim(force: true)` or age the slot and sweep, assert counts return to the
  pre-test baseline, and assert a stale deregistration closure is a no-op.
- Use `LeakWatcher.WatchWithSlots()` when trim is part of the expected cleanup;
  plain `LeakWatcher.Watch()` checks registration counters only. Slot deltas are
  measured against the watcher's own snapshot, so the bus need not be empty.
- Put direct reclamation fixtures in `Tests/Runtime/MemoryReclaim` with
  `[Category("MemoryReclaim")]`. The category is opt-in and, like `Stress`,
  `Performance`, and `Allocation`, suppresses the default-suite wall-clock
  assertion when selected.
- Allocation coverage belongs in `AllocationMatrixTests`: emitting after a
  partial trim stays zero-allocation, and repeated forced trim is idempotent
  after the first reclaim. Pin idempotence through `IMessageBus.TrimResult`
  eviction counts - `AllocationMatrixTests.RepeatedForcedTrimIsIdempotentAfterReclaim` -
  NOT a `GC.Alloc` count budget, which was warm-editor-flaky.
- Allocation tests spanning message kinds must use `MessageScenarios.AllKinds`.

### Documentation duty

When any trigger file changes (`DxMessagingRuntimeSettings.cs`,
`DxMessagingRuntimeSettingsProvider.cs`, `IMessageBus.cs` Trim/slot/TrimResult
members, `MessageHandler.TrimAll`, `Runtime/Core/Pooling/**`,
`Runtime/Core/Configuration/**`), update in the SAME change:
`docs/guides/memory-reclamation.md`, the per-setting table in
`docs/reference/runtime-settings.md` (cross-checked by
`validate:runtime-settings-docs`), and the existing `## [Unreleased]` runtime
memory-reclamation bullet in `CHANGELOG.md` - mutate that bullet rather than
stacking a new one. There is no automated drift gate; verify by hand that every
setting has a row. If `validate:changelog:coverage` raises `W002`, rewrite the
entry around user impact.

## References

| Document                                                              | Purpose                                                                                                            |
| --------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| [memory-reclamation.md](./references/memory-reclamation.md)           | Holder inventory table, counter-based eviction policy, pool layer, and the add-a-MessageCache checklist            |
| [memory-reclaim-coverage.md](./references/memory-reclaim-coverage.md) | Required test proof per holder, `LeakWatcher.WatchWithSlots`, the `MemoryReclaim` category, and allocation budgets |
| [memory-reclamation-docs.md](./references/memory-reclamation-docs.md) | Trigger files and the docs plus CHANGELOG updates required in the same change                                      |
