# What's New in 3.x

[Back to Index](index.md) | [Overview](overview.md) | [Migration Guide](../guides/migration-guide.md)

---

This page highlights the user-visible improvements in the DxMessaging **3.x**
line, written for people building games -- not a line-by-line history. For the
complete, authoritative list (including patch fixes and in-progress work), see
[`CHANGELOG.md`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/CHANGELOG.md). New to DxMessaging? Start with the
[Visual Guide](visual-guide.md) and the [Migration Guide](../guides/migration-guide.md)
instead; nothing here is required reading to get going.

The public API you already use is unchanged across 3.x. The one behavior change
worth knowing is the `ToggleMessageHandler` clarification below; everything else
is faster, safer, or new opt-in surface.

## Faster dispatch, still zero-allocation

Message dispatch and registration are faster across the 3.x line, with the
largest dispatch gains when one message reaches several handlers. Ordinary
typed steady-state struct dispatch still allocates no managed objects.
Stack-trace diagnostics and struct global accept-all dispatch are bounded
allocating paths; the [allocation FAQ](../reference/faq.md#does-dxmessaging-allocate-memory-is-dispatch-zero-gc)
describes those exceptions.

Delivery behavior is unchanged: handlers keep their priority and registration
order, and registration changes made during an emission take effect on the next
emission. The [Performance](../architecture/performance.md) page shows the
current Standalone IL2CPP results. Compare Editor and player numbers only within
the same backend.

## Catch lifecycle mistakes before you run

A common, painful bug is forgetting to call `base.OnEnable()` (or `base.Awake()`,
`base.OnDisable()`, `base.OnDestroy()`, `base.RegisterMessageHandlers()`) in a
`MessageAwareComponent` subclass -- the registration token is never created and
handlers silently never fire. 3.x ships a Roslyn analyzer that flags exactly this
as you type:

- Diagnostics **DXMSG006-DXMSG010** cover the missing base call, lifecycle methods
  hidden with `new`, implicit hiding without `override`, and base-call chains that
  never reach `MessageAwareComponent`. Each message names the concrete runtime
  consequence (token never created, handlers not re-enabled, leak, ...).
- An **inspector overlay** surfaces the same warnings as a HelpBox in the
  component header, without clobbering your own `[CustomEditor]`, and restores the
  last report immediately on Editor startup.
- A runtime breadcrumb logs one loud editor error if a component's token is null
  on enable, catching the case where the analyzer was disabled or never ran.
- Need to opt out deliberately? Apply
  [`[DxIgnoreMissingBaseCall]`](../reference/analyzers.md#dxmsg008-opt-out-marker)
  to a class or method;
  the suppression stays auditable.

Severity is tunable per project in `.editorconfig` (for example
`dotnet_diagnostic.DXMSG006.severity = error`). Every diagnostic is documented in
the [Analyzers reference](../reference/analyzers.md).

## Project-wide settings

A new **DxMessaging settings asset**, reachable from Unity's Project Settings,
centralizes global diagnostics targets, the editor message-buffer size, the
domain-reload warning suppression, the base-call analyzer toggle and ignore list,
and the optional Unity console bridge that feeds the inspector overlay.

## Dedicated editor diagnostics windows

The Inspector is no longer the only diagnostics surface. 3.x also adds
dedicated windows under **Tools > Wallstop Studios > DxMessaging**:

- **Message Monitor** shows recent emissions with message type, route kind,
  context, stack trace, search filters, component details, and JSON export. Use
  Live mode while reproducing a problem or Snapshot mode to inspect buffered
  history.
- **Flow Graph** maps messages to loaded-scene `MessagingComponent` receivers.
  Select a message, component, or route to inspect delivery activity, source
  locations, context, and recent trace evidence. Direct bus/token registrations
  outside those components require bus logs or counters. Filtering and JSON
  export help with dense scenes.

See the [Diagnostics guide](../guides/diagnostics.md) for the current semantics.
Trace paths are recent evidence aggregates, not a replacement for a full
architecture diagram.

## Tune memory reclamation

3.x adds runtime controls for the bus's internal pools so long-running games can
reclaim memory from churned registrations:

- Eviction cadence, enablement, trim opt-out, and pool caps load from a runtime
  settings resource and hot-reload without recreating the bus.
- `IMessageBus.Trim(force)` and `MessageHandler.TrimAll(force)` reclaim empty
  slots and shared pools on demand; idle sweeps run automatically.
- `OccupiedTypeSlots` / `OccupiedTargetSlots` expose the retained footprint for
  diagnostics, alongside new `RegisteredInterceptors`, `RegisteredPostProcessors`,
  and `RegisteredGlobalAcceptAll` counters.

See the [Memory Reclamation guide](../guides/memory-reclamation.md) and the
[Runtime Settings reference](../reference/runtime-settings.md).

## Re-register after a release

`MessageAwareComponent.ReregisterOnEnableAfterRelease` is a new opt-in virtual
property (default `false`, so existing behavior is preserved). Override it to
return `true` and a component whose registration token was released -- for example
via `MessagingComponent.Release` -- re-creates its token and replays
`RegisterMessageHandlers` the next time it is enabled, instead of staying
permanently unregistered. The replay runs exactly once per release; ordinary
enable/disable cycles never re-stage registrations.

## Clearer ToggleMessageHandler semantics (behavior change)

`MessagingComponent.ToggleMessageHandler(false)` is **no longer silently ignored**
while `emitMessagesWhenDisabled` is true. Explicit toggle calls now always win, in
both directions. The Unity enable/disable lifecycle (not your explicit call) is
what skips the handler toggle while `emitMessagesWhenDisabled` is true, so
disabling the component still keeps emission alive -- the flag's documented
purpose -- and an explicit `ToggleMessageHandler(false)` is no longer reverted by a
later enable/disable cycle.

One consequence to know when upgrading: if you set `emitMessagesWhenDisabled`
while the handler is lifecycle-deactivated (disabled with the flag clear), a later
enable no longer auto-reactivates the handler. Call `ToggleMessageHandler(true)`
to resume.

## Unity 6.4+ and 6.5 ready

The Unity object identity backing the dispatch key now reads through a single
version-gated internal helper (`InstanceId.StableId`). On Unity 6.4+ it uses the
non-deprecated `EntityId.ToULong(...)` accessor (keeping the exact 32-bit value
the legacy `GetInstanceID()` returned); older Unity keeps `GetInstanceID()`. This
removes the deprecation warning on 6.4+ and keeps the package compiling on Unity
6.5+, where `GetInstanceID()` becomes a compile error. The dispatch key, equality,
and hashing are unchanged, and the supported floor stays Unity 2021.3. See the
[Compatibility reference](../reference/compatibility.md).

## Easier dependency-injection wiring

Explicit-factory registration helpers landed for all three supported containers:
`VContainerRegistrationExtensions.RegisterDxMessagingBus`,
`ReflexRegistrationExtensions.AddDxMessagingBus`, and
`ZenjectRegistrationExtensions.BindDxMessagingBus`. Each registers the bus under
both the concrete `MessageBus` and the `IMessageBus` interface in one call,
accepts a user-supplied factory, and accepts an `IDxMessagingClock` overload so
tests can inject a fake clock through the container.

## Better tooling and docs

- An [Analyzers reference](../reference/analyzers.md) documents every `DXMSG###`
  diagnostic with severity, triggers, and code samples.
- A shipped `llms.txt` plus README guidance lets AI assistants load accurate
  DxMessaging context.
- Deregistration-speed benchmarks now measure teardown alongside registration, so
  the published [Performance](../architecture/performance.md) tables cover the full
  lifecycle.

## Upgrading

There are no required code changes to move within 3.x. Review the
[ToggleMessageHandler behavior change](#clearer-togglemessagehandler-semantics-behavior-change)
above, then consult [`CHANGELOG.md`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/CHANGELOG.md) for the complete list of
changes and any in-progress work. Adopting DxMessaging for the first time? The
[Migration Guide](../guides/migration-guide.md) walks through incremental
adoption.
