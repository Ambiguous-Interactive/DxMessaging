# Runtime Settings

[Back to Reference](reference.md) | [Memory Reclamation Guide](../guides/memory-reclamation.md) | [Runtime Configuration](../advanced/runtime-configuration.md)

---

The `DxMessagingRuntimeSettings` ScriptableObject controls memory-reclamation
policy and pool sizing for DxMessaging. This page is the canonical reference
for the asset, its parameters, and the public APIs that consume them.

For tuning guidance and scenario-driven recommendations, see the
[Memory Reclamation guide](../guides/memory-reclamation.md).

---

## Overview

`DxMessagingRuntimeSettings` is a ScriptableObject that ships with the package
and is loaded once per AppDomain during the first message-bus construction
via `Resources.Load<DxMessagingRuntimeSettings>("DxMessagingRuntimeSettings")`.
On a load miss the runtime hands out a defaulted in-memory instance so the
package always has a usable settings object. Field changes raise
`SettingsChanged`, which live buses subscribe to so they can re-apply caps
without recreation. The asset is hot-reloadable: edits saved to disk while
Play mode is running take effect on the next sweep boundary.

In non-Unity builds (where `UNITY_2021_3_OR_NEWER` is not defined) the
provider returns `null` because `ScriptableObject` is unavailable. Callers
must tolerate a null result outside Unity.

---

## Asset Location

The asset must live under any `Resources/` folder so that `Resources.Load`
can find it at runtime. The recommended path is:

```text
Assets/Resources/DxMessagingRuntimeSettings.asset
```

Two creation paths put the asset in place:

- `Assets > Create > Wallstop Studios > DxMessaging > Runtime Settings`. The
  asset's `[CreateAssetMenu]` entry; the asset is created in the currently
  selected folder. Move it under a `Resources/` folder afterwards.
- `Assets > Create > Wallstop Studios > DxMessaging > Runtime Settings (in Resources)`.
  The editor menu helper that creates
  `Assets/Resources/DxMessagingRuntimeSettings.asset` directly, creating the
  `Assets/Resources` folder if it does not already exist.

The asset's `OnValidate` warns when an asset path lies outside a `Resources/`
folder because `Resources.Load` would not find it there.

---

## Parameter Reference

| Name                           | C# property                   | Type  | Default                                      | Min | Tooltip                                                                                                                                                                                                                 | Hot-reload | When to change                                                                                                                |
| ------------------------------ | ----------------------------- | ----- | -------------------------------------------- | --- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------- |
| Idle Eviction Seconds          | `IdleEvictionSeconds`         | float | 30.0                                         | 0   | Idle threshold in seconds. Empty per-message-type slots are evicted only after going at least this long without a register/deregister/dispatch touch.                                                                   | Yes        | Lower for leak diagnosis or aggressive reclaim; raise for high-throughput scenarios where slots empty briefly between bursts. |
| Buffer Max Distinct Entries    | `BufferMaxDistinctEntries`    | int   | 512                                          | 0   | Soft cap on the number of distinct entries each shared collection pool will retain. Excess entries are evicted (LRU or LIFO depending on BufferUseLruEviction).                                                         | Yes        | Lower on memory-constrained targets; raise when profiling shows pool churn from a small cap.                                  |
| Buffer Use LRU Eviction        | `BufferUseLruEviction`        | bool  | true                                         | --  | When true, shared collection pools use LRU eviction; otherwise pools behave as a bounded LIFO stack.                                                                                                                    | Yes        | Switch to LIFO when access patterns are short-lived bursts; keep LRU for steady-state reuse.                                  |
| Enable Trim API                | `EnableTrimApi`               | bool  | true                                         | --  | When true, IMessageBus.Trim performs its work; when false it is a no-op returning default. Lets shipped titles disable on-demand reclamation.                                                                           | Yes        | Disable in shipped titles that do not call Trim and do not want third-party code to force a sweep.                            |
| Eviction Tick Interval Seconds | `EvictionTickIntervalSeconds` | float | 5.0                                          | 0   | Minimum interval in seconds between idle sweeps. Emit-time idle eviction samples the clock periodically instead of at the top of every Emit, and sweeps only when this much wall time has elapsed since the last sweep. | Yes        | Raise to reduce sweep frequency on busy hot paths; lower for tighter reclaim cadence.                                         |
| Eviction Enabled               | `EvictionEnabled`             | bool  | true                                         | --  | Master switch for idle-time eviction. When false neither inline emit-time sweeps nor PlayerLoop sweeps run; explicit Trim still works (gated by EnableTrimApi).                                                         | Yes        | Disable when you only want explicit Trim to reclaim, or during editor safe-mode bring-up.                                     |
| Message Buffer Size            | `MessageBufferSize`           | int   | `IMessageBus.DefaultMessageBufferSize` (100) | 0   | Diagnostic message buffer size used when the bus is constructed. Mirrors IMessageBus.DefaultMessageBufferSize so the runtime asset can override the global default without touching code.                               | Yes        | Raise for longer history when debugging; set to 0 to discard emission history and skip the ring buffer.                       |

The `Min` column reflects the `[Min(...)]` attribute that the editor enforces
on numeric fields. Editor-time `OnValidate` clamps negative values back to
zero before raising `SettingsChanged`.

---

## Public Constants

`DxMessagingRuntimeSettings` exposes four `public const` fields so scripts can
reference the same defaults the asset ships with:

| Constant                             | Type     | Value                          | Purpose                                                               |
| ------------------------------------ | -------- | ------------------------------ | --------------------------------------------------------------------- |
| `ResourceName`                       | `string` | `"DxMessagingRuntimeSettings"` | Resource name (no extension) used by `Resources.Load`.                |
| `DefaultBufferMaxDistinctEntries`    | `int`    | 512                            | Default soft cap on per-pool retained entries.                        |
| `DefaultIdleEvictionSeconds`         | `float`  | 30                             | Default idle threshold in seconds before an empty slot becomes stale. |
| `DefaultEvictionTickIntervalSeconds` | `float`  | 5                              | Default minimum interval between idle sweeps, in seconds.             |

Reference these constants from test fixtures and bootstrap code rather than
duplicating literal values.

---

## Public Diagnostic API

These APIs let runtime and test code inspect occupancy and request explicit
reclamation. All four are stable public surface.

### `IMessageBus.OccupiedTypeSlots`

```csharp
int OccupiedTypeSlots { get; }
```

Number of currently occupied per-message-type slots on this bus. Includes
scalar handler sinks, context handler sinks (one count per (type, dictionary),
not per (type, target)), interceptor type slots, and dirty-empty
typed-handler slots. Aggregated on read by walking the per-kind caches; the
cost is O(n) in the number of distinct message types known to the bus.
Snapshot at region boundaries rather than reading in a tight loop.

### `IMessageBus.OccupiedTargetSlots`

```csharp
int OccupiedTargetSlots { get; }
```

Number of currently occupied per-context target or source slots on this bus.
Counts (type, target) tuples: five distinct message types each with the
same target ID counts as 5, not 1. Same aggregation behavior as
`OccupiedTypeSlots`; the cost is O(n) in the number of distinct message
types known to the bus.

### `IMessageBus.Trim`

```csharp
TrimResult Trim(bool force = false);
```

Reclaim empty message slots and pooled collections owned by this bus. When
`force` is true, the call ignores idle-age thresholds and drains shared pools
to zero. When `force` is false, only slots past the configured idle threshold
are eligible. The call is a no-op returning `default(TrimResult)` when
`EnableTrimApi` is false.

Non-Unity and headless hosts must call this periodically when they need
deterministic reclamation. The automatic PlayerLoop sweep hook is only
installed on Unity 2021.3 or newer player and editor hosts.

### `MessageHandler.TrimAll`

```csharp
public static IMessageBus.TrimResult TrimAll(bool force = false);
```

Convenience wrapper that calls `Trim` on the global message bus. Same `force`
semantics as `IMessageBus.Trim`.

### `IMessageBus.TrimResult`

```csharp
public readonly struct TrimResult : IEquatable<TrimResult>
{
    public int TypeSlotsEvicted { get; }
    public int TargetSlotsEvicted { get; }
    public int PooledCollectionsEvicted { get; }
    public int LiveTypeSlotsRemaining { get; }
}
```

| Field                      | Description                                                                                                    |
| -------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `TypeSlotsEvicted`         | Number of typed-handler slots reset across all reclaimed message types.                                        |
| `TargetSlotsEvicted`       | Number of bus target or source context entries removed.                                                        |
| `PooledCollectionsEvicted` | Number of pooled collections dropped from shared pools (`DxPools` plus the bus-owned context-dictionary pool). |
| `LiveTypeSlotsRemaining`   | Number of occupied type slots remaining after the trim; equal to a post-trim read of `OccupiedTypeSlots`.      |

The struct overrides `ToString()` and implements equality on all four fields.

---

## Hot-Reload Semantics

`DxMessagingRuntimeSettings` raises the static event
`DxMessagingRuntimeSettings.SettingsChanged` from its editor `OnValidate` and
from `DxMessagingRuntimeSettingsProvider.Override`. Subscribers should be
small and re-entrancy-safe; the event is invoked synchronously on the calling
thread.

```csharp
public static event Action<DxMessagingRuntimeSettings> SettingsChanged;
```

Existing buses subscribe to the event and call `ApplyRuntimeSettings`, which
re-applies the eviction toggles, idle threshold, tick interval, pool caps,
retention mode, and message buffer size without recreating the bus. The
shared `DxPools` pools and the bus-owned context-dictionary pool reapply the
new caps on the next reclaim opportunity. Live registrations are not
disturbed.

The `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` hook clears
`SettingsChanged` subscribers when a new domain loads, preventing stale
subscriptions from previous Play mode sessions from firing.

---

## Test Override

`DxMessagingRuntimeSettingsProvider.Override(DxMessagingRuntimeSettings settings)`
pushes a test-supplied settings instance as the active `Current` value and
returns an `IDisposable`. Disposing the token restores the previous instance
(LIFO; if a deeper override was pushed on top, dispose is a no-op until the
deeper override is popped first). Both the push and the pop raise
`SettingsChanged` so subscribed buses re-apply caps in both directions.

```csharp
using System;
using DxMessaging.Core.Configuration;
using UnityEngine;

public static class TestSettingsExample
{
    public static void RunWithCustomSettings()
    {
        DxMessagingRuntimeSettings testSettings =
            ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
        // Test-only: production code should not call Override directly.
        using (IDisposable token =
            DxMessagingRuntimeSettingsProvider.Override(testSettings))
        {
            // Bus reads testSettings until token is disposed.
        }
    }
}
```

The provider is intended for tests and bootstrap code that needs to inject a
specific configuration. Production code should rely on the loaded asset (or
the defaulted fallback) rather than calling `Override` directly.

---

## See Also

- [Memory Reclamation guide](../guides/memory-reclamation.md)
- [Runtime Configuration](../advanced/runtime-configuration.md)
- [Diagnostics](../guides/diagnostics.md)
- [Performance](../architecture/performance.md)
