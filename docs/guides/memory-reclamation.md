# Memory Reclamation

DxMessaging keeps dispatch state in per-message-type and per-context slots so
lookups stay O(1) on the hot path. Long-running sessions can otherwise retain a
slot for every message type or `InstanceId` ever touched. The memory
reclamation system bounds that growth without changing dispatch semantics or
allocating during emit.

This page describes when reclamation runs, what it touches, how to configure
it for common scenarios, and the diagnostic counters you can use to verify it
is doing its job.

## Table of Contents

- [Memory Reclamation](#memory-reclamation)
  - [Overview](#overview)
  - [Quick Start](#quick-start)
  - [How Reclamation Works](#how-reclamation-works)
  - [Tuning by Scenario](#tuning-by-scenario)
  - [Reading TrimResult](#reading-trimresult)
  - [Diagnostics Counters](#diagnostics-counters)
  - [Troubleshooting](#troubleshooting)
    - [I called Trim but nothing changed](#i-called-trim-but-nothing-changed)
    - [Memory keeps growing across scenes](#memory-keeps-growing-across-scenes)
    - [Idle sweeps don't seem to fire](#idle-sweeps-dont-seem-to-fire)
  - [See Also](#see-also)

---

## Overview

Reclamation targets two kinds of state:

- **Empty handler and interceptor slots** kept on the bus per message type and,
  for targeted and broadcast messages, per `InstanceId`. A slot becomes empty
  after every registration that used it has been deregistered. Empty slots are
  retained until reclamation runs because a freshly empty slot is often about
  to be used again on the next dispatch.
- **Pooled collections** held by `DxPools` and the bus-owned context-dictionary
  pool. Pools cap their retained entries with either LRU or bounded LIFO
  retention.

Active registrations are never reclaimed. A handler that has not been
deregistered, an interceptor that is still wired up, or a typed-handler slot
with at least one live registration is treated as live state and left alone,
no matter how old it is. Reclamation only resets slots that are already empty.

The system exists for long-running sessions. Editor play sessions, dedicated
servers, and shipped titles that keep the same process running across many
scene changes accumulate distinct message types and target `InstanceId`s over
time. Without reclamation, those slots stay around for the lifetime of the
process. With reclamation, idle empty slots are reset on a sweep cadence you
control, and shared collection pools stay below the configured cap.

---

## Quick Start

Default behavior works without any setup. The first bus construction calls
`Resources.Load<DxMessagingRuntimeSettings>("DxMessagingRuntimeSettings")`
and, on a miss, hands out a defaulted in-memory instance so the package runs
out-of-the-box. If you do not need to change defaults, you do not need an
asset.

To customize, create the asset:

1. In the Project window, run `Assets > Create > Wallstop Studios > DxMessaging > Runtime Settings`.
   The default `[CreateAssetMenu]` path creates the asset in the currently
   selected folder.
1. Move or place the asset under any `Resources/` folder. The file name must
   stay `DxMessagingRuntimeSettings.asset` because that is the resource name
   `Resources.Load` looks up.
1. Alternatively, use `Assets > Create > Wallstop Studios > DxMessaging > Runtime Settings (in Resources)`
   from the menu bar. That helper creates
   `Assets/Resources/DxMessagingRuntimeSettings.asset` directly, ensuring the
   asset is picked up at runtime.

The recommended path is `Assets/Resources/DxMessagingRuntimeSettings.asset`.
The asset's editor `OnValidate` warns when it is placed outside a `Resources/`
folder because `Resources.Load` cannot find it there.

Field changes raise `DxMessagingRuntimeSettings.SettingsChanged`. Live buses
re-apply caps, retention modes, and toggles without recreation, so editing the
asset while the editor is in Play mode takes effect on the next sweep
boundary.

For the full list of fields, defaults, and tooltip text, see the
[Runtime Settings reference](../reference/runtime-settings.md).

---

## How Reclamation Works

There are two reclamation paths and they share the same underlying sweep code.
A sweep reclaims only empty slots; active registrations are never touched.

### Idle Sweep

Idle sweeps run on a wall-clock cadence. Two triggers can drive a sweep:

- **Emit-time sampling.** Every emit checks whether enough wall time has
  elapsed since the last sweep. The threshold is
  `EvictionTickIntervalSeconds`. When it has, the bus runs a sweep before
  dispatching the message. Sampling the clock periodically keeps the per-emit
  overhead at one branch on the hot path (every 16th emit samples the wall
  clock to decide whether enough time has elapsed).
- **Unity PlayerLoop hook.** `EvictionPlayerLoopHook` inserts a sweep callback
  into Unity's PlayerLoop so that idle sweeps still run when no emits are
  happening. The hook is installed automatically on Unity 2021.3 and newer
  player and editor hosts. Non-Unity hosts must drive cadence by emitting
  messages or calling `Trim` directly.

Idle sweeps are gated by two settings. When `EvictionEnabled` is false neither
the inline emit-time path nor the PlayerLoop path runs. When
`EvictionTickIntervalSeconds` is large, sweeps run less often.

Empty slots become eligible only after they have remained empty (and free of
register, deregister, or dispatch activity) for at least `IdleEvictionSeconds`
worth of bus activity ticks (advanced on emit, register, deregister, and once
per frame from the Unity PlayerLoop). The bus tracks per-slot dirty state by
stamping touched slots with an internal tick counter, then revisits only the
types, targets, interceptors, and handlers that have changed since the
previous sweep.

On non-Unity hosts the tick counter only advances on bus activity, so an
inactive bus does not age out empty slots without an explicit `Trim`. Drive
sweeps by emitting messages periodically or call `Trim` from a maintenance
thread.

### Explicit Trim

Two public APIs reclaim synchronously:

- `IMessageBus.Trim(bool force = false)` runs a sweep on a single bus.
- `MessageHandler.TrimAll(bool force = false)` is the convenience entry point
  for the global bus.

When `force` is true, the sweep ignores the idle threshold and reclaims every
empty candidate immediately, including draining shared collection pools to
zero. When `force` is false, the explicit call uses the same idle threshold as
the sweep cadence, so it acts as an opportunistic top-up rather than a
heavy-handed flush.

Both APIs return a `TrimResult` so callers can log or assert what was
reclaimed. The master switch `EnableTrimApi` controls whether explicit trim
performs work; when the switch is false, both APIs become a no-op that returns
a default `TrimResult`. `EnableTrimApi` and `EvictionEnabled` are independent.
A shipped title can keep idle sweeps on while disabling the explicit API, or
disable idle sweeps and reclaim only at scene boundaries.

---

## Tuning by Scenario

The defaults (30 second idle threshold, 5 second tick, LRU pool retention with
512 distinct entries, both master switches on) are tuned for general-purpose
projects. Use the table below as a starting point when defaults do not fit.

| Scenario                         | Recommended settings                                                                                       | Rationale                                                                                                                                                       | When to call Trim explicitly                                                                                                                                                  |
| -------------------------------- | ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| High-throughput stable types     | `EvictionEnabled = true`, larger `IdleEvictionSeconds` (60 to 120), default tick, default pools            | A small set of message types is touched constantly. Long idle thresholds prevent sweeping slots that briefly empty between bursts.                              | Rarely. Defaults already match this shape.                                                                                                                                    |
| Mobile or low-memory titles      | `BufferMaxDistinctEntries` lowered (128 to 256), `BufferUseLruEviction = true`, default tick, default idle | Shrinking the pool cap caps peak retained pool memory. LRU retains the entries that are actually being reused.                                                  | Call `Trim(force: true)` on scene-load completion to drop pool entries that the loaded scene will not reuse.                                                                  |
| Dynamic types across scenes      | Default eviction, `EnableTrimApi = true`                                                                   | Each scene introduces a different set of targets and message types. Idle eviction handles steady-state cleanup; explicit trim handles transitions.              | `Trim(force: true)` on scene unload, scene additive load, or after a teardown of a scoped subsystem.                                                                          |
| Shipped title with minimal churn | `EvictionEnabled = false`, `EnableTrimApi = true`, `EvictionTickIntervalSeconds` left at default           | When the set of types and targets is fixed at startup, idle sweeps are pure overhead. Keeping the explicit API on lets you reclaim at well-defined transitions. | At explicit transitions: scene boundaries, post-bootstrap, before extended idle screens.                                                                                      |
| Leak diagnosis                   | `EvictionEnabled = true`, low `IdleEvictionSeconds` (1 to 5), low `BufferMaxDistinctEntries` (32 to 64)    | Aggressive caps and a short idle threshold expose slots that are not getting evicted because something is holding a registration live.                          | After a suspected leak, call `Trim(force: true)` and read `OccupiedTypeSlots` and `OccupiedTargetSlots`. Slots that survive a forced trim correspond to active registrations. |
| Editor safe-mode                 | `EvictionEnabled = false`, `EnableTrimApi = false`                                                         | Domain reload races and editor-time enter/exit play transitions can race a running sweep. Disabling both switches removes that risk in safe-mode bring-up.      | Re-enable the switches after the editor stabilizes.                                                                                                                           |

The settings asset hot-reloads, so you can change a row of this table by
editing the asset rather than restarting the editor.

---

## Reading TrimResult

Every successful `Trim` call returns an `IMessageBus.TrimResult`. Its fields
are read-only counters for the work the sweep performed:

- `TypeSlotsEvicted` is the number of typed-handler-slot entries that were
  reset across all reclaimed message types.
- `TargetSlotsEvicted` is the number of bus-side target or source context
  entries removed across all reclaimed `InstanceId`s.
- `PooledCollectionsEvicted` is the number of pooled collections dropped from
  shared pools (`DxPools` plus the bus-owned context-dictionary pool).
- `LiveTypeSlotsRemaining` is the count of occupied type slots remaining on
  the bus after the sweep. This is the same value `OccupiedTypeSlots` returns
  immediately after the sweep.

The struct also overrides `ToString()` so it renders as a single line in
logs.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using UnityEngine;

public static class MemoryReclamationLogger
{
    public static void LogForcedTrim()
    {
        IMessageBus.TrimResult result = MessageHandler.TrimAll(force: true);
        Debug.Log($"[DxMessaging] {result}");
        Debug.Log(
            $"[DxMessaging] reclaimed {result.TypeSlotsEvicted} type slots, "
                + $"{result.TargetSlotsEvicted} target slots, "
                + $"{result.PooledCollectionsEvicted} pooled collections; "
                + $"{result.LiveTypeSlotsRemaining} type slots still live."
        );
    }
}
```

For non-global buses, call `bus.Trim(force: true)` and inspect the result the
same way.

---

## Diagnostics Counters

Two public counters on `IMessageBus` report current slot occupancy:

- `OccupiedTypeSlots` is the count of distinct per-message-type slots that are
  currently occupied on the bus.
- `OccupiedTargetSlots` is the count of distinct target or source context
  slots that are currently occupied on the bus.

Both counters are aggregated on read. The implementation walks the per-kind
caches, so the call is O(n) in the number of message types or targets known
to the bus. Snapshot the values at region boundaries (start of a scene
unload, end of a leak-watching scope) rather than polling them every frame.

These counters integrate with the internal test-suite `LeakWatcher` utility
(see `Tests/Runtime/TestUtilities/LeakWatcher.cs` for the pattern; users can
build their own equivalent for production diagnostics). A typical
verification pattern:

1. Snapshot `OccupiedTypeSlots` and `OccupiedTargetSlots` at the start of a
   scoped operation.
1. Run the operation.
1. Call `Trim(force: true)` so empty slots are reset.
1. Compare the post-trim counters against the snapshot. Any difference
   represents registrations that survived the operation; either intentional
   or a leak.

The `TrimResult.LiveTypeSlotsRemaining` field is identical to a post-trim
read of `OccupiedTypeSlots` and is the cheaper option when you have just
called `Trim`.

---

## Troubleshooting

### I called Trim but nothing changed

`Trim` only reclaims empty slots. If a message type's typed-handler slot still
has at least one active registration, the slot is preserved. Confirm with
`OccupiedTypeSlots` before and after the call and check that the registrations
you expected to be torn down were actually deregistered.

If the result really is empty, check `EnableTrimApi` on the active settings
asset. When it is false, both `IMessageBus.Trim` and
`MessageHandler.TrimAll` return a default `TrimResult` without doing any
work. Set it to true (the default) or use a different settings asset.

If empty slots exist but were not reclaimed, the call may have run with
`force: false` and slots may not yet have aged out. Pass `force: true` to
ignore `IdleEvictionSeconds` and reclaim every empty candidate.

### Memory keeps growing across scenes

Cross-scene growth usually comes from new `InstanceId`s introduced by each
scene. Idle eviction will eventually reclaim the empty slots, but you can
reclaim deterministically at the transition. Call
`MessageHandler.TrimAll(force: true)` (or `bus.Trim(force: true)` for
non-global buses) on scene unload, after the previous scene's components have
finished tearing down. The result's `TargetSlotsEvicted` and
`PooledCollectionsEvicted` fields confirm that the transition's targets were
cleared.

If forced trim does not reduce the counters, an active registration is
keeping the slot live. Audit components that survive scene boundaries
(singletons, `DontDestroyOnLoad` objects, container-managed services) and
verify their tokens are deregistered when their owners go away.

### Idle sweeps don't seem to fire

Three settings gate idle sweeps:

- `EvictionEnabled` must be true. When it is false neither the inline
  emit-time path nor the PlayerLoop hook runs.
- `EvictionTickIntervalSeconds` controls the minimum interval between sweeps.
  A very large value defers sweeps; zero allows back-to-back sweeps but does
  not force one to run on every emit.
- `IdleEvictionSeconds` controls when an empty slot becomes eligible. If the
  threshold is larger than the interval between dispatches that touch the
  slot, the slot is repeatedly reset and never ages out. Lower the threshold
  or call `Trim(force: true)` for a deterministic reclaim.

In non-Unity hosts, the PlayerLoop hook is unavailable. Either drive sweeps
by continuing to emit messages periodically or call `Trim` from a maintenance
thread.

---

## See Also

- [Runtime Settings reference](../reference/runtime-settings.md)
- [Runtime Message Bus Configuration](../advanced/runtime-configuration.md)
- [Diagnostics](diagnostics.md)
- [Performance](../architecture/performance.md)
