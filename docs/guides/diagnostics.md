# Diagnostics

DxMessaging emphasizes visibility. You can enable diagnostics globally or per
token, inspect recent emissions, page through registrations, view contexts
(targets/sources), monitor the global bus, and export a filtered message-flow
graph from Unity's editor tools.

## DiagnosticsTarget Enum

The `DiagnosticsTarget` enum is a flags enum that controls when diagnostics are enabled. It allows fine-grained control over which execution environments collect diagnostic data.

| Value     | Description                                                     |
| --------- | --------------------------------------------------------------- |
| `Off`     | Diagnostics are disabled in all environments.                   |
| `Editor`  | Diagnostics run only while in the Unity Editor.                 |
| `Runtime` | Diagnostics run only in player/runtime builds (not the Editor). |
| `All`     | Diagnostics run in both Editor and runtime environments.        |

Because `DiagnosticsTarget` is a flags enum, you can combine values:

```csharp
using DxMessaging.Core.MessageBus;

// Enable diagnostics only in the Unity Editor
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;

// Enable diagnostics only in runtime builds
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Runtime;

// Enable diagnostics everywhere
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;

// Disable diagnostics completely
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
```

## Configuration Toggles

DxMessaging provides multiple levels of diagnostics control:

### Global Defaults

- `IMessageBus.GlobalDiagnosticsTargets` -- Sets the default diagnostics mode for newly created buses and tokens. Uses the `DiagnosticsTarget` flags enum.
- `IMessageBus.GlobalMessageBufferSize` -- Sets the default ring buffer size for emission history (default: 100).

### Per-Bus and Per-Token

- `IMessageBus.DiagnosticsMode` -- Read-only property indicating whether diagnostics are active for a specific bus instance.
- `MessageRegistrationToken.DiagnosticMode` -- Controls diagnostics for an individual registration token.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

// Configure global defaults before creating buses/tokens
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
IMessageBus.GlobalMessageBufferSize = 200;

// Check if diagnostics are enabled for a specific bus
IMessageBus bus = MessageHandler.MessageBus;
if (bus.DiagnosticsMode)
{
    Debug.Log("Diagnostics are active on this bus.");
}
```

### Project Settings

The package registers a UI Toolkit Project Settings page under
**Project Settings > Wallstop Studios > DxMessaging**. Use it to set the same
project-wide defaults without writing bootstrap code:

- **Diagnostics Targets** -- the `DiagnosticsTarget` flags enum.
- **Message Buffer Size** -- the default diagnostics ring-buffer size.
- **Suppress Domain Reload Warning** -- editor-safety warning control.
- **Base-Call Check Enabled** and **Use Console Bridge** -- Inspector warning
  controls for missing `MessageAwareComponent` base calls.

The ignore list for base-call warnings still lives on
`Assets/Editor/DxMessagingSettings.asset`. See the
[Inspector Overlay & Base-Call Warnings](inspector-overlay.md#project-settings-panel)
guide for the field-by-field Inspector behavior.

## Editor Tools

The Inspector remains useful for the selected `MessagingComponent`, but the
current editor tooling also includes two dedicated windows under
**Tools > Wallstop Studios > DxMessaging**.

### Message Monitor

Open **Tools > Wallstop Studios > DxMessaging > Message Monitor** to inspect the
default global bus. The monitor shows recent global emissions in most-recent
first order with message type, context, stack trace, filtering, selected-entry
details, manual refresh, visible message-type/context lanes, and **Copy JSON**
export.
The filter keeps existing plain text matching and also supports complete
whitespace-separated field facets backed by captured entry data: `type:`,
`message:`, `context:`, and `stack:`. Facet terms can be combined, for example
`type:Damage context:Player`. Quote typed values with spaces, for example
`context:"Context: Player"`; unquoted values with spaces stay on the plain text
path.
The active filter strip shows whether the current filter is typed or plain text
and provides a Clear action without changing JSON export.
Message-type lanes group the currently visible entries by message type, then
show entry count, distinct context count, entry share, and context list for each
message type so a filtered monitor view shows which message volume dominates.
Context lanes group the same visible entries by context, then show entry count,
distinct message-type count, entry share, and message list for each context.
Each lane row includes a Filter action that updates the monitor filter in place:
message-type lanes apply a `type:` filter, and context lanes apply a quoted
exact `context:` filter for the visible context.

The lower component diagnostics panel summarizes loaded scene
`MessagingComponent` instances without resolving serialized providers. It shows
listener counts, enabled/diagnostics listener counts, registrations, call counts,
local message counts, provider status, and provider warnings such as a missing
serialized provider or a provider that resolves no bus.

If the active global bus is not the default concrete DxMessaging `MessageBus`,
the message list reports that it is unavailable. Component diagnostics still use
safe editor capture and avoid mutating provider state.

### Flow Graph

Open **Tools > Wallstop Studios > DxMessaging > Flow Graph** to inspect loaded
scene `MessagingComponent` registration topology. The graph aggregates:

- component nodes,
- message-type nodes,
- registration edges by message type, target component, and registration kind,
- route-map call shares,
- visible message lanes by message type,
- visible target lanes by target component,
- visible trace route-kind lanes by traced registration kind,
- visible trace message lanes by traced message type,
- visible trace target lanes by traced target component,
- visible trace-id lanes by positive trace id,
- visible trace context lanes by normalized context,
- recent global and listener-local emission evidence,
- exact recent traced delivery counts per registration edge,
- recent trace-path/context evidence when diagnostics captured token delivery
  records with positive trace ids.

The graph supports filtering, stable row selection, details for selected
components/messages/routes, and **Copy JSON** export. The Visible Message Lanes
panel groups visible registration edges by message type, then reports route
count, distinct target count, registration count, calls, recent traced
deliveries, no-call routes, route kinds, call share, target paths, and inactive
target breadth for each lane. The Visible Target Lanes panel groups visible
registration edges by target component, then reports route count, distinct
message count, registration count, calls, recent traced deliveries, no-call
routes, route kinds, call share, target id, active state, and message list for
each lane. The Visible Flow Corridors panel groups visible trace paths by
message and target component, then reports path count, context count, trace-id
count, route kinds, traced deliveries, and delivery share for each corridor. The
Visible Trace Route Kind Lanes panel groups visible trace paths by traced
registration kind, then reports path count, distinct message, target, and
normalized context counts, distinct trace-id count, traced deliveries, delivery
share, message list, target list, and normalized context list for each route
kind lane. Blank registration kinds collapse into `<unknown route kind>` so
legacy or malformed trace-path evidence remains visible. The
Visible Trace Id Lanes panel groups visible trace paths by positive trace id,
then reports path-membership count, distinct message, target, and normalized
context counts, route kinds, path-membership share, message list, target list,
and normalized context list for each trace id lane. It intentionally uses path
memberships rather than delivery shares because the trace-path aggregate records
which positive trace ids touched a path, not per-trace-id delivery volume. The
Visible Trace Message Lanes panel groups visible trace paths by traced message
type, then reports path count, normalized context count, distinct target count,
distinct trace-id count, route kinds, traced deliveries, delivery share,
normalized context list, and target list for each trace message lane. The
Visible Trace Target Lanes panel groups visible trace paths by traced target
component, then reports path count, distinct message and normalized context
counts, distinct trace ids, route kinds, traced deliveries, delivery share,
message list, and normalized context list for each trace target lane. The
Visible Trace Context Lanes panel groups visible trace paths by normalized trace
context, then reports path count, distinct message and target counts, distinct
trace ids, route kinds, traced deliveries, delivery share, message list, and
target list for each context lane. The Route Map summary reports the visible
route-kind mix, the widest visible message by distinct target components, the
target component with the most visible inbound routes, inactive routed targets,
the hottest visible route by call share, and visible routes with no calls. It
also reports how many visible routes have at least one recent traced delivery and
which visible route accounts for the largest share of recent traced deliveries,
plus which visible message accounts for the largest share of recent route-edge
traced deliveries and which visible target accounts for the largest share. Both
the Route Map and Recent Trace Paths summaries report the visible trace context
count, busiest context by recent traced deliveries, distinct visible trace ids,
widest visible trace id by path count, and busiest visible trace message,
target, and trace path plus each one's trace-path delivery share. The Message
Lanes, Target Lanes, and Route Map route-kind, route, call, target-component
fan-out, target fan-in, inactive routed-target, no-call, recent traced-route,
and recent-traced delivery counts remain scoped to the visible routes while
corridor, trace-id-lane, trace-message-lane, trace-target-lane, context-lane,
and trace message/target/path summaries remain scoped to visible trace paths. Selected
component and message details include route-health counts for traced routes,
routes with no calls, and the busiest traced route by visible route-edge
delivery share. Selected component details also name the busiest traced message,
and selected message details name the busiest traced target from their visible
route-edge traced deliveries. They also report the selected component or message
share of visible recent route-edge traced deliveries. Selected component,
message, and route details also list matching trace contexts, report context
count and busiest context volume, group recent traced deliveries by context,
report the busiest context's share of their matching trace-path deliveries,
show matching trace-id breadth, name the busiest matching trace path, and report
that path's delivery share so you can see which captured source/target context
accounts for the matching trace-path delivery volume. Selected component details
also name the busiest trace message and its matching trace-path delivery share,
and selected message details name the busiest trace target and its matching
trace-path delivery share. Selected route details also report the exact route's
share of visible recent route-edge traced deliveries.
The current export uses
`schemaVersion: 5`, `captureMode` set to
`registration-topology-with-recent-diagnostics`, and a `traceSemantics` field
that explains how trace ids, per-trace-path trace-id counts, and exact
per-trace-path trace-id arrays are interpreted.

Trace paths are recent evidence aggregates built from token-side delivery
records that carry a positive `traceId`. They group by concrete delivered
message type, context, target component, and registration type. They are not a
durable producer-to-consumer architecture model; records created manually or
outside a concrete `MessageBus` dispatch have `traceId = 0` and cannot
participate in a trace path. The widest-trace summary counts visible paths that
share a positive numeric trace id; it is recent captured-record evidence, not a
durable bus-identity guarantee.

## RegistrationLog API

The `RegistrationLog` class tracks all messaging registrations and deregistrations for a message bus. This is invaluable for debugging subscription issues and understanding message flow.

### Properties

| Property        | Type                                   | Description                                                             |
| --------------- | -------------------------------------- | ----------------------------------------------------------------------- |
| `Enabled`       | `bool`                                 | Get/set whether logging is active. Disabled by default for performance. |
| `Registrations` | `IReadOnlyList<MessagingRegistration>` | Read-only access to all logged registrations.                           |

### Methods

#### `Log(MessagingRegistration registration)`

Records a registration event. Called automatically by the message bus when `Enabled` is true.

#### `GetRegistrations(InstanceId instanceId)`

Returns all registrations for a specific instance. Useful for inspecting what a particular component has registered for.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

IMessageBus bus = MessageHandler.MessageBus;
bus.Log.Enabled = true;

// After some registrations occur...
InstanceId myComponent = GetComponent<MonoBehaviour>();
foreach (MessagingRegistration reg in bus.Log.GetRegistrations(myComponent))
{
    Debug.Log($"Registered for {reg.type.Name} via {reg.registrationMethod}");
}
```

#### `ToString()` and `ToString(Func<MessagingRegistration, string> serializer)`

Returns a string representation of all logged registrations. You can provide a custom serializer for formatted output.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

IMessageBus bus = MessageHandler.MessageBus;
bus.Log.Enabled = true;

// ... after some registrations/deregistrations
Debug.Log(bus.Log.ToString());

// Custom formatting
string formatted = bus.Log.ToString(reg =>
    $"[{reg.registrationType}] {reg.type.Name} @ {reg.time:F2}s"
);
Debug.Log(formatted);
```

#### `Clear(Predicate<MessagingRegistration> shouldRemove = null)`

Removes registrations from the log. Pass `null` to clear all, or provide a predicate to selectively remove entries.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

IMessageBus bus = MessageHandler.MessageBus;

// Clear all registrations
int cleared = bus.Log.Clear();

// Clear only deregistrations
int deregistrationsCleared = bus.Log.Clear(
    reg => reg.registrationType == RegistrationType.Deregister
);
```

## MessagingRegistration Struct

Each logged registration is stored as a `MessagingRegistration` struct containing:

| Field                | Type                 | Description                                                  |
| -------------------- | -------------------- | ------------------------------------------------------------ |
| `id`                 | `InstanceId`         | The handler's unique identifier.                             |
| `type`               | `Type`               | The message type being registered for.                       |
| `registrationType`   | `RegistrationType`   | Whether this was a `Register` or `Deregister` event.         |
| `registrationMethod` | `RegistrationMethod` | The exact registration category (Targeted, Broadcast, etc.). |
| `time`               | `float`              | Unity time when the registration occurred (Unity only).      |

### RegistrationMethod Values

The `RegistrationMethod` enum captures how the handler was wired up:

- `Targeted` -- Bound to a specific recipient
- `Untargeted` -- Global untargeted handler
- `Broadcast` -- Bound to a specific source
- `BroadcastWithoutSource` -- Broadcast handler without explicit source
- `TargetedWithoutTargeting` -- Targeted handler ignoring runtime target
- `GlobalAcceptAll` -- Catch-all handler
- `Interceptor` -- Message interceptor
- `UntargetedPostProcessor`, `TargetedPostProcessor`, `BroadcastPostProcessor` -- Post-processors
- `TargetedWithoutTargetingPostProcessor` -- Post-processor for targeted messages ignoring runtime target
- `BroadcastWithoutSourcePostProcessor` -- Post-processor for broadcasts without explicit source

## Emission History

When diagnostics are enabled, buses and tokens record message emissions in a ring buffer:

- Buffer size is controlled by `IMessageBus.GlobalMessageBufferSize` (default: 100).
- Setting buffer size to 0 disables history retention (emissions are silently discarded).
- Inspect recent emissions per token via built-in diagnostics or build custom tools using post-processors.
- Bus-side `MessageBus` records carry a non-zero `traceId` while dispatching.
- Token-side records carry the observing `registrationHandle` and, when the
  delivery happened during a concrete bus dispatch, the same non-zero `traceId`.
- Manually-created records and legacy direct handler dispatches keep
  `traceId = 0`; tools treat those as local evidence only.

```csharp
using DxMessaging.Core.MessageBus;

// Increase buffer size for more history
IMessageBus.GlobalMessageBufferSize = 500;
```

## Logging Integration

Integrate DxMessaging with your logging framework:

```csharp
using DxMessaging.Core;

MessagingDebug.enabled = true;
MessagingDebug.LogFunction = (level, msg) =>
    UnityEngine.Debug.Log($"[DxMessaging:{level}] {msg}");
```

## Per-Environment Configuration

A common pattern is enabling diagnostics only in the Editor for development visibility while keeping runtime builds lean.

### Editor-Only Diagnostics

```csharp
using DxMessaging.Core.MessageBus;

// Enable diagnostics only when running in the Unity Editor
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
```

This is the recommended default for most projects. You get full visibility during development without any performance cost in production builds.

### Runtime Diagnostics for QA Builds

For QA or debug builds where you need diagnostics in the player:

```csharp
using DxMessaging.Core.MessageBus;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
#else
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
#endif
```

### Conditional Logging Based on Build Type

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

public static class DiagnosticsBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
#if UNITY_EDITOR
        IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
        IMessageBus.GlobalMessageBufferSize = 200;
        MessageHandler.MessageBus.Log.Enabled = true;
#elif DEVELOPMENT_BUILD
        IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Runtime;
        IMessageBus.GlobalMessageBufferSize = 50;
#else
        IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
#endif
    }
}
```

## Performance Considerations

Diagnostics add overhead. Consider these factors when enabling them:

### Memory Impact

- Each `MessagingRegistration` struct consumes memory for the registration log.
- The emission ring buffer stores `MessageEmissionData` records (controlled by `GlobalMessageBufferSize`).
- Larger buffer sizes consume more memory but provide more history.

### CPU Impact

- Registration logging adds overhead to every `Register` and `Deregister` call.
- Emission recording adds overhead to every message broadcast.
- Post-processor chains for diagnostics run after each message dispatch.

### Recommendations

| Environment        | Recommended Setting                     | Buffer Size |
| ------------------ | --------------------------------------- | ----------- |
| Development/Editor | `DiagnosticsTarget.Editor`              | 100-200     |
| QA/Debug Builds    | `DiagnosticsTarget.All`                 | 50-100      |
| Release Builds     | `DiagnosticsTarget.Off`                 | N/A         |
| Automated Tests    | `DiagnosticsTarget.All` + `Log.Enabled` | 100         |

```csharp
using DxMessaging.Core.MessageBus;

// Production-safe defaults
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
IMessageBus.GlobalMessageBufferSize = 0; // No history retention
```

## Editor Integration (Inspector)

Attach `MessagingComponent` to a GameObject. In the Unity Inspector:

- **Enable/Disable Global Diagnostics**: Toggles bus-wide recording.
- **Global Buffer**: Paged view of recent emissions (type and context). Matching listeners are highlighted.
- **Local Buffer**: Per-listener ring buffer; enable per-token diagnostics to populate.
- **Registrations**: Paged list of what each listener registered for (type, priority, context).

## Tips

- Turn on diagnostics while developing; turn off for release builds if you don't need runtime recording.
- Use Message Monitor when you need the latest global bus emissions and stack
  traces.
- Use Flow Graph when you need registration topology, route-map call shares,
  route-kind mix, widest target-component fan-out, hottest visible routes,
  most-routed targets, inactive routed-target hints, no-call route hints, visible
  traced-route coverage, busiest traced-route, traced-message, and traced-target
  share, visible trace route-kind lanes, visible trace message lanes, visible
  trace target lanes, visible trace-id lanes, visible trace context volume and
  share, visible trace context lanes, visible trace-id breadth, visible flow
  corridors, visible trace-message/target/path concentration,
  selected component/message route-health and busiest traced-route details,
  selected component busiest traced-message details, selected message busiest
  traced-target details, selected component trace-message and selected message
  trace-target details, selected component/message/route visible traced-share
  details, selected component/message/route trace context volume and deliveries,
  busiest-context shares, trace-id breadth, busiest paths, and busiest-path shares, and recent
  trace-path evidence, including distinct trace-id counts, widest visible trace ids,
  busiest trace-message, trace-target, and trace-path shares, for
  loaded scene components.
- Use `RegisterTargetedWithoutTargeting` or `RegisterBroadcastWithoutSource` for custom monitoring dashboards.
- Set `Log.Enabled = true` in tests to verify registration behavior.
- Use `Log.Clear()` between test cases to isolate registration tracking.

## Memory diagnostic counters

Three pieces of API expose memory-reclamation state on `IMessageBus`:

- `OccupiedTypeSlots` returns the number of distinct per-message-type slots
  currently occupied on the bus.
- `OccupiedTargetSlots` returns the number of distinct target or source
  context slots currently occupied on the bus.
- `Trim(bool force = false)` reclaims empty slots and returns a `TrimResult`
  whose `TypeSlotsEvicted`, `TargetSlotsEvicted`,
  `PooledCollectionsEvicted`, and `LiveTypeSlotsRemaining` fields describe
  the work performed. `MessageHandler.TrimAll(force)` is the convenience
  wrapper for the global bus.

Both counters aggregate on read by walking the per-kind caches; the cost is
O(n) in the number of distinct message types known to the bus. Snapshot the
values at region boundaries (start of a scene unload, end of a leak-watching
scope) rather than polling them every frame.

A typical leak-watching pattern uses these counters together with the
internal test-suite `LeakWatcher` utility (see
`Tests/Runtime/TestUtilities/LeakWatcher.cs` for the pattern; users can build
their own equivalent for production diagnostics):

1. Snapshot `OccupiedTypeSlots` and `OccupiedTargetSlots` at the start of a
   scoped operation.
1. Run the operation.
1. Call `Trim(force: true)` to reset every empty slot.
1. Compare the post-trim counters against the snapshot. Surviving slots
   correspond to active registrations.

For the full reclamation model, tuning recommendations, and worked examples,
see the [Memory Reclamation guide](memory-reclamation.md).

## Related

- [Listening Patterns](../concepts/listening-patterns.md)
- [Inspector Overlay & Base-Call Warnings](inspector-overlay.md)
- [Memory Reclamation](memory-reclamation.md)
- [Runtime Settings Reference](../reference/runtime-settings.md)
- [Troubleshooting](../reference/troubleshooting.md)
