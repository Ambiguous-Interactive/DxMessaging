# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Three per-kind **marginal registration** benchmark scenarios
  (`UntargetedRegistration_Marginal`, `TargetedRegistration_Marginal`,
  `BroadcastRegistration_Marginal`) that measure the GC-allocation cost of an additional
  same-type registration on an already-warm bus -- the surface the registration
  allocation reductions target -- so the published performance tables now show per-kind
  registration cost (allocation count + bytes), not just dispatch throughput. Each
  registers 1000 more handlers of one warmed message type using distinct, pre-built
  handler delegates, so the measured window captures only the registration machinery
  (never the handler delegate, and never a same-handler refcount bump). They are
  wall-clock (zero-throughput) rows whose allocation columns populate on the
  profiler-bearing in-editor PlayMode/Mono leg and read `n/a` on the published Standalone
  IL2CPP Release leg (which strips the profiler), exactly like the existing
  registration-flood rows.
- Benchmarks now report the total allocated BYTES per measurement batch (`gcAllocatedBytes`)
  alongside the existing managed-allocation CALL count (`gcAllocations`). Bytes are
  measured from a before/after delta of the live Unity
  `ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame").CurrentValue`
  -- a within-frame `GC.Alloc`-hook byte accumulator that SUMS allocation-hook bytes
  rather than measuring a heap-size difference, so it is exact (verified: 100 x
  `byte[10000]` reads 1,003,200 bytes, run-to-run identical) and immune to mid-window
  collections (a heavy-churn region that swung a `GC.GetTotalMemory` delta to -133 MB
  read a stable 8,000,000 bytes here). The byte counter is profiler-dependent
  like the count probe: in the editor and development players it is functional, and on
  the published non-development Standalone IL2CPP Release leg -- where the profiler is
  stripped -- BOTH metrics report the `Unmeasured` sentinel (rendered `n/a`), never a
  fabricated `0`. Bytes are INFORMATIONAL -- the perf-delta PR comment renders byte
  deltas goodness-signed (`N fewer bytes` / `N more bytes`) -- while the regression gate
  stays on the allocation COUNT, which remains the canonical signal (Unity's own
  Performance Testing package likewise reads the alloc-CALL count, not bytes). The
  cross-library comparison tables now also surface this byte metric as a dedicated
  GC-allocated-bytes matrix, and the Unity `SendMessage` comparison boxes its value
  payload on every dispatch (as any real `SendMessage(value)` call must) so its
  allocation columns show that real per-call cost instead of a misleading zero. New API:
  `AllocationProbe.MeasureWithBytes` returning `AllocationProbe.AllocationSample`,
  `AllocationProbe.Window.SampleBytes()`/`SampleBoth()`,
  `AllocationProbe.BytesFunctional`, `BenchmarkMeasurement.GcAllocatedBytes`,
  `ColdLatencyMeasurement.MedianGcAllocatedBytes`, and
  `MinimumMeasurement<T>.GcAllocatedBytes`.
- New "What's New in 3.x" documentation page (Getting Started) that summarizes
  the user-visible improvements across the 3.x line -- faster zero-allocation
  dispatch, the base-call analyzer and inspector overlay, project-wide settings,
  memory-reclamation controls, `ReregisterOnEnableAfterRelease`, the clarified
  `ToggleMessageHandler` semantics, Unity 6.4+/6.5 compatibility, and the
  dependency-injection helpers -- with links to the authoritative changelog and
  the migration guide.

### Fixed

- The token-creation allocation guard no longer flakes in a warm editor. The
  diagnostics-lazy win (token `Create` allocating 7 managed objects instead of 11) was
  guarded by an absolute `GC.Alloc` recorder COUNT budget, but a warm, long-lived editor
  domain attributes background allocations to whatever measurement window is open --
  measured on the host editor, the minimum over 64 windows was ~19 allocations for that
  7-allocation operation (median ~51), well above any budget tight enough to catch the
  revert -- so the guard false-failed run-to-run. A measure-first probe also disproved the
  assumption that the swing came from the `DxPools` collection pools: the steady refcount
  registration path does not rent from them at all (hits = misses = 0), so the noise is
  pure background-editor `GC.Alloc` pollution that no pool pre-warm can remove. The guard
  is now a DETERMINISTIC state assertion -- after `Create` with diagnostics off, the lazy
  `_callCounts`/`_emissionBuffer` backing fields must still be `null` -- which uses no
  allocation probe, never flakes, and runs in the per-PR EditMode correctness leg rather
  than the weekly perf-gated Allocation scope.
- Two more memory-reclamation/registration allocation guards no longer flake in a warm
  editor. The forced-trim guard measured a `GC.Alloc` COUNT budget over a 32-trim window
  and the dirty-target reuse guard a per-batch count budget; both false-failed run-to-run
  on the same warm-editor ambient-noise floor that the token-creation guard hit. The
  forced-trim guard is now a DETERMINISTIC assertion on the exact `IMessageBus.TrimResult`
  eviction counts (the first force-trim reclaims; every subsequent one is an idempotent
  no-op that evicts nothing and leaves the live slot count stable) plus a `DxPools`
  Misses-flat check (repeated no-op trims rent no fresh pooled collection); the
  dirty-target guard now relies solely on its existing exact pool Hits/Misses assertions
  (renting the warmed collection, never allocating a fresh one). Neither uses an allocation
  probe, so neither flakes. The two deterministic registration STORAGE structural guards
  (the by-value metadata parameter and the staging-function map value type) were also moved
  from the weekly perf-gated Allocation suite into the per-PR EditMode correctness leg, so
  they protect every PR. Internal test/quality change only; no public API or runtime
  behavior change.
- Benchmark and library-comparison allocation reporting is now honest. The harness
  measured allocations with `GC.GetAllocatedBytesForCurrentThread()`, which returns
  `0` for every allocation under Unity's Boehm GC (verified: a forced 1 MB array
  allocation read back a `0`-byte delta), so the published "allocated bytes" column
  was a vacuous `0` for every technology -- hiding real per-operation allocations
  (Unity `SendMessage`, for example, boxes its value-type payload once per call).
  The metric is now a COUNT of managed allocations from the reliable `GC.Alloc`
  profiler recorder (new `AllocationProbe`), renamed `gcAllocations`, and reports an
  `Unmeasured` sentinel (rendered `n/a`) -- never a fabricated `0` -- when no probe
  is available on a backend. The perf-doc/PR-comment renderers, the regression gate,
  and the methodology docs were updated accordingly; the doc's allocation column
  reads `n/a` until the next CI run repopulates real counts.
- Source generators (`[DxUntargetedMessage]`, `[DxTargetedMessage]`,
  `[DxBroadcastMessage]`, `[DxAutoConstructor]`) now work in projects that do not use
  assembly definitions. The generator and analyzer DLLs previously shipped under the
  editor-only `Editor/Analyzers/` folder, which Unity scopes to the package's editor
  assembly and assemblies that reference it -- so a consumer's runtime code in
  `Assembly-CSharp` (or a runtime asmdef referencing only the runtime assembly) never
  received the generator, and `[Dx*Message]` types failed to implement their generated
  interface with cryptic `CS0315`/`CS0452` errors. The labeled DLLs now ship under
  `Runtime/Analyzers/` (governed by the all-platforms runtime assembly), so Unity
  applies the generator to the DxMessaging runtime assembly and every assembly that
  references it, including the predefined `Assembly-CSharp`. No assembly definition is
  required. Closes GitHub issue #229.
- Documentation no longer teaches the obsolete `IMessageBus.GlobalDiagnosticsMode`
  API. The reference, patterns, glossary, and migration guides now use
  `IMessageBus.GlobalDiagnosticsTargets` with the `DiagnosticsTarget` flags enum
  (matching the canonical diagnostics guide), and the API reference no longer shows a
  write to the read-only `IMessageBus.DiagnosticsMode` property. A source-derived
  drift-guard (`DocsObsoleteApiReferenceTests`) now fails the build if any published
  doc references a member marked `[Obsolete]` in the runtime, so this cannot regress.

### Changed

- Registration now allocates one fewer managed object per handler in the common case. The
  registration token stored every handle's de-registration in a per-handle holder object,
  but the common case is EXACTLY ONE de-registration per handle, so the single
  de-registration delegate is now stored INLINE in the token's de-registration map (with no
  holder object); a holder is allocated only when a rare second de-registration accumulates
  on the same handle (the re-entrant retarget-recovery replay). The ordering,
  partial-failure, and rollback-baseline semantics are unchanged (pinned by the existing
  `PendingDeregistrationStorageTests` and the full re-entrancy suite), and a new
  `RegistrationStorageStructuralGuardTests.DeregistrationsStoreInlineActionNotPerHandleHolder`
  deterministically guards the inline storage against regression. Internal change only; no
  public API or behavior change.
- **BREAKING (v4):** the `IMessageBus` registration contract now returns an opaque,
  zero-allocation handle instead of a deregistration delegate. The 14
  `IMessageBus.Register*<T>` methods return a new `readonly struct`
  `DxMessaging.Core.MessageBus.MessageBusRegistration` (previously `System.Action`), and a
  new `void IMessageBus.Deregister<T>(in MessageBusRegistration registration) where T : IMessage`
  undoes a registration (call it with the same `T` you registered with; for
  `RegisterGlobalAcceptAll`, any `T : IMessage` works since the global slot is not
  type-keyed). This removes the per-registration bus-side deregistration **closure** (the
  prior `Action` plus its display class) from the handler registration path -- roughly
  **two fewer managed allocations per registration** -- by packing the deregistration
  snapshot into the returned value handle and re-expressing the bus's deregistration logic
  against it (behaviour and the four reentrancy invariants -- generation guard, identity
  liveness / over-deregistration, token idempotency, no counter underflow -- are unchanged).
  Custom `IMessageBus` implementers that wrap `MessageBus` (the
  `DelegatingMessageBus`-style extension point) just forward the new members; from-scratch
  implementers mint handles via the public `MessageBusRegistration(long, object)`
  constructor and read them back in their own `Deregister<T>`.
  **The Unity-facing surface is unaffected:** `MessageRegistrationToken`,
  `MessageAwareComponent`, and the `MessageHandler.Register*` facades keep their existing
  shapes and still return `System.Action`. Only code that talks to `IMessageBus.Register*`
  **directly** and keeps the returned delegate sees the break. The larger
  per-registration allocation reduction (collapsing the token/handler closures onto a
  single per-handle object) is sequenced as a follow-up on top of this contract change.
- The published performance report no longer prints columns of `n/a`. The
  Standalone IL2CPP leg runs in a Release player whose stripped profiler cannot
  measure GC allocations or bytes, so the renderer now OMITS a memory column from a
  per-scope dispatch table when every row is unmeasured (the Standalone table is
  throughput-only), omits a whole cross-library memory matrix when no leg measured
  that metric, and drops the unmeasured allocation/byte segment from each per-PR
  delta cell -- instead of filling them with a wall of `n/a`. The real allocation
  and byte numbers still publish from the in-editor PlayMode (Mono) leg, and `n/a`
  now appears only as a genuine per-row or per-library cell (a metric measured for
  the scope in general but missing for that one entry).
- Registration allocates less. Each registration token's diagnostics-only
  call-count and emission-history collections are now created lazily instead of
  eagerly, so a token whose owner never enables diagnostics (the default) no longer
  pays for them -- token creation drops from 11 to 7 managed allocations. The
  per-registration metadata is now passed by value instead of through a closure
  factory that was invoked immediately, removing one delegate allocation per handler
  registration. Steady-state dispatch stays allocation-free, and diagnostics, the
  inspector overlay, and all lifecycle behavior are unchanged.
- Registering a handler OR a post-processor with the convenience `Action<T>` /
  `Action<InstanceId, T>` overloads now allocates one fewer closure per registration.
  This covers the handler registers (`RegisterUntargeted`, `RegisterTargeted`,
  `RegisterTargetedWithoutTargeting`, the sourced-broadcast register, and
  `RegisterBroadcastWithoutSource`) and the matching post-processor registers
  (`RegisterTargetedPostProcessor`, `RegisterTargetedWithoutTargetingPostProcessor`, the
  GameObject/Component/source broadcast post-processors, and
  `RegisterBroadcastWithoutSourcePostProcessor`). Each such registration previously built
  two delegates -- a diagnostics-augmented `Action` wrapper plus a separate by-ref
  `FastHandler` adapter for the flat-dispatch path -- and now folds diagnostics into a
  single by-ref `FastHandler` closure that is the dispatch target directly (also removing a
  per-dispatch indirection). The public API is unchanged; the optimization is internal and
  verified by differential allocation guards that pin the `Action` registration cost to the
  already-optimal `FastHandler` registration cost. Diagnostics, deduplication, and dispatch
  ordering are preserved.
- Every registration kind now allocates about two fewer managed objects. The token used to
  wrap each staged registration in a per-registration parameterless `Action` (a delegate plus
  its display class) whose only job was to re-bundle the handle, the staging function, and the
  de-registration bookkeeping; the token now stores each staging function directly and pairs it
  with its handle in the replay queue, so that wrapper -- and the closure `InternalRegister`
  needed to build it -- is gone. Measured cold-registration floor (FastHandler, diagnostics off):
  untargeted drops from 14.69 to 12.69 managed allocations per registration (a clean -2.00), with
  the same ~2-allocation reduction across targeted, broadcast, without-targeting/source, and
  post-processor registrations (~12% fewer registration allocations overall). The de-registration
  replay, rollback-on-failure, re-entrancy, and equal-priority registration-order semantics are
  unchanged; the public API is unchanged. Pinned structurally by a deterministic guard that the
  token stores the staging function (not an `Action` wrapper).
- Every active registration now allocates about two fewer managed objects again. The token
  tracked each handle's live de-registration in a per-handle holder that eagerly allocated a
  `List` (plus its backing array) to hold what is almost always a single de-registration; the
  holder now keeps that one de-registration inline and only allocates an overflow list on the
  rare second de-registration for the same handle. Measured cold-registration floor (FastHandler,
  diagnostics off): untargeted drops from 13.29 to 11.29 managed allocations per registration (a
  clean -2.00), with the same ~2-allocation reduction across targeted and broadcast. The
  insertion-order, partial-failure-retryable, and rollback-baseline de-registration semantics are
  unchanged (verified by a differential simulation across every count/failure/start-index
  combination); the public API is unchanged.
- Each handler/post-processor registration now allocates one fewer managed object by replacing
  its per-registration de-registration **closure** with a per-handle **object**. The typed
  handler used to hand back a captured parameterless `Action` (a delegate plus the display class
  holding the slot, cache key, priority, and generation it closed over) as the thing the token
  would later invoke to tear the registration down; that closure is now a single per-handle
  `HandlerDeregistration` object whose `Deregister()` instance method carries the same captured
  state as plain fields (and converts implicitly to `Action`, so the token's replay/rollback
  machinery is untouched). To let one non-generic object tear down a generic
  `HandlerActionCache<TU>` without re-introducing a per-handler-delegate-type closure, the erased
  `IHandlerActionCache` gained three non-generic operations (`ContainsEntry`, `BumpVersion`,
  `DeregisterEntry`). The de-registration **order** (generation guard, slot-version liveness,
  keyed-vs-scalar split, identity/over-de-registration check, version bump, bus de-register,
  refcount decrement) is a faithful re-expression of the prior closure body, pinned by the 19
  arbitrary-order `MixedOrderDeregistrationTests` and the full re-entrancy suite. Because these
  `MessageHandler.Register*` facades now return the internal `HandlerDeregistration` (still
  implicitly an `Action`), they were narrowed from `public` to `internal` -- this is part of the
  v4 bus<->handler boundary rework and does NOT touch the Unity-facing surface
  (`MessageRegistrationToken`, `MessageAwareComponent`); only code calling
  `MessageHandler.Register*` **directly** is affected.
- Every registration kind now allocates roughly half as many managed objects again by
  collapsing the token's per-handle staging closures into one unified per-handle object.
  Each registration previously staged a `Func<handle, HandlerDeregistration>` (a delegate plus
  its display class capturing the target/source, user handler, and priority) whose nested
  `AugmentedHandler` local function became a _second_ delegate (the diagnostics-augmented flat
  invoker). Both are now a single per-handle `Registration` object stored in `_registrations`:
  its fields hold the captured staging state, and its diagnostics-augmented invoker is an
  _instance method_ bound to the object -- so `MessageHandler` still receives a `FastHandler<T>`
  delegate and the hot dispatch path stays delegate-based (no virtual/interface call per
  dispatch). A `Register()` method runs a kind-switch to call the matching
  `MessageHandler.Register*` and reproduce the exact prior staging body per kind. Because the
  constrained `MessageHandler.Register*<T>` calls require `T : ITargetedMessage` /
  `IUntargetedMessage` / `IBroadcastMessage` (which a single `Registration<T> where T : IMessage`
  cannot satisfy), the object is realized as three constraint-family generic subclasses plus one
  non-generic global-accept-all subclass over a non-generic `Registration` base -- still a
  unified per-handle object with a kind-switch, not a per-method subclass explosion. Measured
  marginal cost (cold, FastHandler, diagnostics off) drops from ~9.3 to ~4.6 managed allocations
  per registration (about half). The equal-priority registration-order dispatch, idempotent
  double-deregister, partial-failure rollback, generation/slot-version guards, refcount handlers,
  and diagnostics call-counts/emission semantics are all unchanged (pinned by the 19
  `MixedOrderDeregistrationTests` and the full re-entrancy suite); the public API is unchanged.
  Pinned structurally by a deterministic guard that `_registrations` stores the unified
  `Registration` object, not a `Func`/`Action` wrapper.
- The bug-report issue template now offers the package version as a dropdown of
  released versions (with an `Other` fallback) instead of a free-text field, so
  reports carry an exact, valid version. The list is generated from
  `package.json`, `CHANGELOG.md`, and git tags, kept in sync by
  `npm run check:issue-template-versions` (gated in `validate:all`), and
  self-heals on the default branch via the `Update Issue Template Versions`
  workflow. Closes GitHub issue #230.
- The Roslyn source generator no longer copies its DLLs into the consumer's
  `Assets/Plugins/Editor/` folder on editor load; it (with its pinned Roslyn dependency
  DLLs) ships ready-to-use under the package's `Runtime/Analyzers/` folder
  (RoslynAnalyzer-labeled, excluded from player builds). Projects upgrading from an
  earlier version have the redundant in-project copy removed automatically during asset
  import, before script compilation, so the package's copy and the old in-project copy
  never both run the generator (which would otherwise duplicate generated members). No
  manual action is required: the cleanup only removes the
  `Assets/Plugins/Editor/WallstopStudios.DxMessaging` folder when it contains the
  first-party source-generator DLL plus exact known legacy analyzer/dependency DLL names
  the package created, and leaves any foreign DLL, foreign `.meta`, subfolder, or
  other content untouched. If a mixed or incomplete legacy payload is detected, the
  editor logs one warning with manual cleanup guidance instead of deleting the folder
  silently.

## [3.1.0]

### Added

- Deregistration-speed benchmarks (`DeregistrationFlood_1000Types_Cold` and
  `DeregistrationFlood_1000Types_WarmJit`): the teardown mirror of the existing
  registration floods. Each stages 1000 live registrations untimed, then times
  `MessageRegistrationToken.UnregisterAll()` (the production deregistration path).
  Both are wall-clock (latency) rows alongside the registration floods in the
  rendered dispatch tables (closes the deregistration ask in GitHub issue #31).
- `MessageAwareComponent.ReregisterOnEnableAfterRelease`: opt-in virtual property
  (default `false`, preserving existing behavior). When overridden to return
  `true`, a component whose registration token was released (for example via
  `MessagingComponent.Release`) re-creates its token and replays
  `RegisterMessageHandlers` the next time it is enabled, instead of staying
  permanently unregistered. The replay runs exactly once per release; plain
  enable/disable cycles without a release never re-stage registrations.

### Changed

- The Unity object identity backing `InstanceId` (the dispatch key) is now read
  through a single version-gated source, `InstanceId.StableId`. On Unity 6.4+ it
  uses the non-deprecated `EntityId.ToULong(...)` accessor and keeps its low 32
  bits -- exactly the value the legacy `GetInstanceID()` returned (verified across
  `GameObject`, `Component`, and `ScriptableObject`); older Unity keeps
  `GetInstanceID()`. This keeps the package compiling on Unity 6.5+, where
  `GetInstanceID()` becomes a compile error, and removes its deprecation warning on
  Unity 6.4+. The 32-bit dispatch key, equality, and hashing are unchanged. Closes
  GitHub issue #208.
- `MessagingComponent.ToggleMessageHandler(false)` is no longer silently ignored
  while `emitMessagesWhenDisabled` is true: explicit toggle calls now always win,
  in both directions. Instead, the Unity enable/disable lifecycle itself now skips
  the handler toggle while `emitMessagesWhenDisabled` is true, so disabling the
  component still keeps emission alive (the flag's documented purpose) AND an
  explicit `ToggleMessageHandler(false)` is no longer reverted by a later
  enable/disable cycle. Previously the veto made explicit deactivation requests
  silent no-ops and `OnEnable` force-reactivated the handler. One consequence:
  setting the flag while the handler is lifecycle-deactivated (disabled with the
  flag clear) means a later enable no longer auto-reactivates - call
  `ToggleMessageHandler(true)` to resume.

- Removed the internal per-handler dispatch-link machinery (the ten
  `*DispatchLink` wrapper classes, their lazily-populated slot array, and
  the outer-generation guard) plus the vestigial non-global prefreeze
  descriptors that the flattened dispatch had already stopped consuming:
  snapshots for non-global slots no longer build per-handler bucket entry
  arrays at all (they dispatch exclusively through the resolved flat
  delegate arrays and keep count-only buckets for the legacy "found any
  handlers" reporting), which removes one pooled array rent/fill/return
  per priority bucket from every snapshot rebuild. No public API or
  dispatch semantics change (verified emission-for-emission against the
  previous implementation, including global accept-all mid-emission
  mutation ordering, trim-then-re-register staleness, reset-mid-dispatch,
  and zero steady-state allocations).
- Each emission now consults a cached per-(bus, message-type, kind) dispatch
  plan instead of re-resolving interceptor, global accept-all, and
  post-processor sinks with multiple type-cache lookups per emit. When the
  plan shows none of those features are present (the common case), a fast
  emit lane runs only the handle phase: one plan fetch, one validity check,
  snapshot acquisition, and the flat dispatch loop. Plans are invalidated by
  a single bus-wide version stamp that every registration, deregistration,
  interceptor/global/post mutation, sweep/Trim, `ResetState`, and runtime
  settings reload bumps; mutations performed by handlers mid-emission are
  re-detected at phase boundaries, so frozen-snapshot semantics, mid-emission
  registration gating, interceptor re-targeting, and mid-dispatch reset
  behavior are unchanged (verified emission-for-emission against the previous
  implementation). Diagnostics mode and `MessagingDebug.enabled` are still
  read live on every emission. Out-of-Unity rig measurements (directional):
  one-handler throughput up roughly 1.5-1.7x per kind and four-handler
  fan-out up ~35%, with feature-heavy paths unchanged within noise and zero
  steady-state allocations.
- The internal flat-dispatch shape assertion (`DebugAssertFlatShape`) moved
  from `DEBUG` builds to the opt-in `DXMESSAGING_INTERNAL_CHECKS` scripting
  define; it cost a type test per dispatch on Editor hot paths.
- On IL2CPP players, the dispatch hot loops (flat snapshot walks, global
  accept-all bucket walks, and global entry invokers) now opt out of the
  generated per-iteration null and array-bounds checks via a vendored
  internal `Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute`. The
  elided checks guard invariants the snapshot builder already guarantees
  (frozen arrays, non-null handler/invoker pairs, `count` never exceeding
  the array length), all pinned by tests; rig/diagnostic builds keep the
  `DXMESSAGING_INTERNAL_CHECKS` shape assertions. No behavior change under
  Mono or in the editor.
- On IL2CPP players, the per-emit AOT untyped-dispatch bridge registration
  (`EnsureAot*Bridge<T>`, IL2CPP-only) now runs when a message type's dispatch
  plan is first built on a bus (the first typed emit) instead of on every emit,
  removing a generic-static class-initialization check and a non-inlined call
  from the steady-state IL2CPP dispatch prologue. The registration is latched
  process-globally, so it actually executes once per type; the guarded call site
  is simply reached at first plan build rather than on every emit. The bridge is
  rooted before the first untyped dispatch of a type by either any registration
  of that type or its first typed emit, so the untyped-dispatch contract is
  unchanged (a never-touched type still throws the same missing-bridge error).
  No behavior change under Mono or in the editor (the calls compile out there).
- Dispatch for every message kind (untargeted, targeted, and broadcast;
  handle and post-process phases) now resolves handlers to flat, pooled
  delegate arrays at snapshot-build time instead of walking per-handler
  dictionaries and dispatch links per message. Measured on Editor PlayMode
  Mono x64 versus the previous release: one untargeted handler 17.5M to
  22.1M emits/sec, four untargeted handlers 3.9M to 20.0M, one targeted
  listener 11.4M to 15.7M, sixteen targeted listeners 0.73M to 8.7M, one
  broadcast handler 8.0M to 15.6M, four post-processors 3.3M to 12.6M -
  all with zero steady-state allocations, an 8% faster cold registration
  flood, and unchanged dispatch semantics (snapshot freezing, priority and
  registration order, mid-emission registration visibility). One deliberate
  refinement: a handler that deactivates itself mid-emission now skips its
  own remaining delegates in that emission for every kind (the active check
  runs per delegate instead of once per handler), matching the documented
  immediate-deactivation semantics. Mid-emission registration gating is
  also now uniform: a delegate registered during an emission never fires in
  that emission, for every kind and every registration shape. Previously a
  handler registering a different-shaped delegate for the same type on its
  own MessageHandler could fire it in the same emission, depending on
  unrelated handler counts.

### Fixed

- Domain-reload advisory warnings are now re-evaluated after deferred Editor
  settings creation/migration, so an initial passive load with no settings asset
  cannot permanently skip an unsuppressed warning for that editor domain.
- Token cleanup now clears token-local diagnostics and stale teardown state:
  `UnregisterAll()`, `Dispose()`, final-handle removal, released
  `MessagingComponent` tokens, and disposed registration leases no longer
  retain metadata, call counts, emission history, or stale deregistration
  closures that could report old registrations or over-deregister later.
  Failed `Enable()` replays now roll back partial registrations before
  throwing the original failure; failed active `RetargetMessageBus()` replays
  roll back partial new-bus registrations and restore previous-bus
  registrations that are not still live behind a failed rollback cleanup.
  Deregistration actions that throw before cleanup remain retryable instead
  of being forgotten, including through owning registration leases and
  `MessagingComponent.Release()` retries. `ActivateOnBuild` failures now
  clean up the partially built lease before throwing again; if cleanup cannot
  complete, `MessageRegistrationBuildException` exposes the retryable lease
  so callers can dispose it after resolving the cleanup failure.
- Interceptors registered through a `MessageRegistrationToken` bound to a
  custom bus (`MessageRegistrationToken.Create(handler, customBus)`) now land
  on that bus. `RegisterUntargetedInterceptor`, `RegisterTargetedInterceptor`,
  and `RegisterBroadcastInterceptor` previously dropped the token's bus and
  registered on the handler's default (typically global) bus, so they never
  saw custom-bus emissions and silently intercepted global traffic instead.
  The fix also covers registrations staged while disabled (they activate on
  the token's bus at `Enable()` time) and `RetargetMessageBus`, which now
  re-routes interceptors along with every other registration kind.
- Registering or deregistering a handler mid-emission and then emitting the
  same message type reentrant-style from inside a handler no longer corrupts
  the in-flight dispatch.
  The nested emission's snapshot rebuild previously released the pooled
  arrays the outer emission was still iterating
  (`NullReferenceException` / `ArgumentOutOfRangeException`, or silent
  cross-dispatch aliasing at deeper nesting). Displaced snapshots are now
  released only after the outermost dispatch exits, each emission keeps its
  own frozen-cache identity across nested emissions, and the broadcast
  priority walk tolerates nested membership churn.
- A `MessageRegistrationLease` whose `OnActivate` callback throws no longer
  wedges its registrations. The lease previously recorded itself as inactive
  while the registrations stayed live, so `Deactivate()` and `Dispose()`
  silently refused to release them. The lease now marks itself active before
  invoking the callback: the exception still propagates, and standard
  `Deactivate()`/`Dispose()` teardown fully releases the registrations.
- Calling `DxMessagingStaticState.Reset` (or resetting a bus) from inside a
  message handler no longer crashes the in-flight emission. Targeted and
  broadcast dispatch previously returned the active emission's pooled snapshot
  arrays mid-iteration (`NullReferenceException` /
  `ArgumentOutOfRangeException`); the teardown is now deferred until the
  outermost dispatch exits, mirroring the existing `Trim` deferral, and the
  remaining handlers in that emission short-circuit cleanly.
- Equal-priority handlers now always dispatch in live registration order. The
  documented "same priority uses registration order" contract previously broke
  after remove/re-register churn (a new handler could dispatch in a removed
  handler's old position), after `Disable()`/`Enable()` cycles (replay order
  permuted), and across components (a newly created component could dispatch
  in a destroyed component's old position). Handler caches, bus-side priority
  buckets, and token replay now preserve insertion order explicitly.
- Targeted and broadcast post-processors now follow an interceptor-rewritten
  target/source. When an interceptor redirects a message via its
  `ref InstanceId` parameter, post-processors registered for the rewritten id
  run (previously the broadcast path never re-resolved the rewritten source,
  and the targeted path preferred a stale pre-interceptor snapshot, so the
  rewritten id's post-processors were silently skipped).
- `DiagnosticsTarget` gating now matches its documented semantics. Player
  builds previously enabled diagnostics for the `Editor` flag and ignored the
  `Runtime` flag, and the Editor enabled diagnostics when only `Runtime` was
  set. `Editor` now enables diagnostics only inside the Unity Editor,
  `Runtime` only in player/runtime builds, and `All` in both, exactly as the
  diagnostics guide describes.
- Analyzer payload builds are now reproducible under the pinned
  `SourceGenerators/global.json` SDK. The shipped analyzer and source-generator
  DLLs no longer embed source revision or PDB metadata, CI double-builds them
  into temporary payload directories before comparing against
  `Editor/Analyzers`, and Unity runner maintenance now verifies or repairs the
  full active editor matrix outside ordinary test jobs.
- IL2CPP builds now root untyped dispatch bridges for concrete
  source-visible message types without changing the public API. The source
  generator emits IL2CPP-only AOT bridge registration for attributed messages
  and manual `IUntargetedMessage` / `ITargetedMessage` /
  `IBroadcastMessage` implementations, while open generic definitions are
  skipped until a closed generic type is used through the typed registration
  path. Untyped dispatch also keeps separate per-kind delegate caches, so a
  message type that participates in more than one dispatch kind can no longer
  reuse the wrong cached delegate shape.
- Unity projects no longer keep stale DxMessaging analyzer entries in `csc.rsp`; the setup script removes package-cache analyzer registrations so Unity loads the shipped analyzer and source generator once through the `RoslynAnalyzer`-labeled plugin copy. The base-call ignore sidecar's `-additionalfile:` entry is also re-synchronized after deferred sidecar regeneration, so first-load `OnValidate` writes and Inspector ignore-list edits repair missing `csc.rsp` wiring during the same editor session.
- Provider-backed emit helpers now route sourced, targeted, and untargeted messages through the resolved `IMessageBus`, so custom bus and DI-provider callers no longer fall back to the global bus for interface-shaped message dispatch.
- Standalone and IL2CPP player builds now compile. The dispatch hot path performs reinterpret casts that previously used `System.Runtime.CompilerServices.Unsafe`; the Unity Editor supplies that type, but player builds under the .NET Standard 2.0 profile do not, so editmode and playmode passed while standalone IL2CPP failed to build with `CS0103: The name 'Unsafe' does not exist in the current context`. Those calls now route through Unity's built-in `UnsafeUtility` (in `UnityEngine.CoreModule`), which resolves identically in the Editor and every player scripting backend. The change preserves the existing zero-allocation dispatch behavior and adds no package dependency or shipped assembly.
- Shipped source generators now compile against Unity 2021-compatible Roslyn 3.8 APIs and use the classic `ISourceGenerator` entry point, preventing Unity 2021.3 analyzer-host load failures (`CS8032`) while preserving the generated message-id and auto-constructor output for newer Unity editors.
- `MessageRegistrationToken.RemoveRegistration(handle)` now compiles cleanly on Unity 2021 while preserving the existing behavior of removing the active deregistration, staged registration, metadata, and diagnostic call-count entries.
- Unity 2021.3 no longer aborts compilation with _Multiple precompiled assemblies with the same name_ (`PrecompiledAssemblyException`). The shipped Roslyn analyzer and source-generator DLLs are now excluded from every build platform (the Editor included) and activated solely by the `RoslynAnalyzer` asset label, so Unity treats them as compiler analyzers rather than managed precompiled assemblies that collide with the copy placed in the consuming project's `Assets/`. Generated message-id and auto-constructor output is unchanged.
- The dependency-injection sample README (`Samples~/DI`) now links to the real `Runtime/Unity/Integrations/VContainer/VContainerRegistrationExtensions.cs`; the previous link dropped the `VContainer/` folder and resolved to a missing file.

## [3.0.1]

### Added

- New Roslyn base-call analyzer (`MessageAwareComponentBaseCallAnalyzer`) that flags `MessageAwareComponent` subclasses whose lifecycle overrides forget to invoke `base.Awake()`, `base.OnEnable()`, `base.OnDisable()`, `base.OnDestroy()`, or `base.RegisterMessageHandlers()`. Introduces diagnostics `DXMSG006` (missing base call), `DXMSG007` (lifecycle method hidden with `new`), `DXMSG008` (opt-out marker), `DXMSG009` (method implicitly hides a lifecycle method without `override`/`new`), and `DXMSG010` (`base.{method}()` chains into an override that does not reach `MessageAwareComponent`). DXMSG006's diagnostic message is now per-method: each guarded method emits a sentence describing the runtime consequence (registration token never created, handlers not re-enabled, memory leak, etc.) so users immediately see what breaks. The inspector overlay HelpBox renders the same per-method sentences. Severity is tunable per project via `.editorconfig` (e.g. `dotnet_diagnostic.DXMSG006.severity = error`). Ships as a separate `WallstopStudios.DxMessaging.Analyzer.dll` deployed alongside the existing source-generator DLL by `SetupCscRsp` so it loads under both Unity 2021's Roslyn 3.8 analyzer host and newer Unity versions. Diagnostic help links now open the current analyzer reference page in the DxMessaging repository.
- Runtime self-check breadcrumb on `MessageAwareComponent`: `OnEnable` now logs a one-time `Debug.LogError` per instance when the registration token is null, with a link to the DXMSG006 reference. Gated on `UNITY_EDITOR || DEBUG`, so release builds pay no cost. Catches the case where the analyzer DLL was disabled or did not run, surfacing the silent failure as a loud editor error instead.
- New public `[DxIgnoreMissingBaseCall]` attribute (`DxMessaging.Core.Attributes`) for source-level opt-out of the base-call analyzer. Applied to a class, every guarded lifecycle method on that class is exempt; applied to a single method, only that method is exempt. The analyzer still emits an Info-level `DXMSG008` at the suppression site so opt-outs remain auditable, and the inspector overlay's snapshot honours the same scoping (method-level suppresses only the annotated method, type-level opts out the entire type). Not inherited -- derived classes must opt out explicitly.
- New inspector overlay (`MessageAwareComponentInspectorOverlay`) for every `MessageAwareComponent` subclass: missing-base-call warnings reported by the analyzer or harvested from the Unity console are surfaced as a HelpBox in the inspector header without clobbering user-defined `[CustomEditor]`s (the overlay hooks `Editor.finishedDefaultHeaderGUI`). The overlay restores the previous session's report immediately on Unity Editor startup (loaded from `Library/DxMessaging/baseCallReport.json`) instead of waiting for the first post-reload scan to complete; the HelpBox is annotated `(cached from previous session -- refreshing...)` until the first scan refreshes it. A companion fallback editor (`MessageAwareComponentFallbackEditor`) hosts the overlay for subclasses with no other custom editor and renders the body via `DrawDefaultInspector()` so subclasses with no serialized fields no longer leave an empty vertical gap below the inspector header.
- New DxMessaging project-wide settings asset (`DxMessagingSettings`, stored at `Assets/Editor/DxMessagingSettings.asset`) accessible from Unity's Project Settings. Controls diagnostics targets applied to `IMessageBus.GlobalDiagnosticsTargets`, the editor message buffer size, the domain-reload warning suppression, the base-call analyzer toggle, the project-wide base-call ignore list, and the optional Unity console bridge that feeds the inspector overlay.
- New `docs/reference/analyzers.md` reference page documenting every `DXMSG###` diagnostic the package emits, with severity, source generator/analyzer, trigger conditions, message text, and code samples for each. Added to the Reference section of the documentation site navigation.
- Added `llms.txt` plus README onboarding guidance so users can connect AI assistants with accurate DxMessaging package context.
- Test-suite hardening: parameterized scenario fixture (`MessageScenario`, `MessageScenarios`, `ScenarioHarness`, `AllocationAssertions`) under `Tests/Runtime/TestUtilities/` enabling kind-parameterized tests.
- Behavioural gap closures: `HandlerExceptionTests`, `ReentrantEmissionTests`, `NullAndInvalidInputTests`, `SingleThreadContractTests` pinning exception-in-handler, re-entrancy, null-input, and threading contracts.
- `AllocationMatrixTests` covering zero-GC dispatch across kinds, interceptors, post-processors, diagnostics, and priority-based dispatch.
- Expanded coverage now pins source-generator and analyzer behaviour that users rely on: generic / record struct / nested partial / nullable annotation cases for `DxMessageIdGenerator`; `[DxOptionalParameter]` permutations and DXMSG005 boundary cases for `DxAutoConstructorGenerator`; positive opt-out cases for `DxIgnoreMissingBaseCallAttribute`. No runtime API change.
- Three new public read-only registration counters on `IMessageBus`: `RegisteredInterceptors`, `RegisteredPostProcessors`, and `RegisteredGlobalAcceptAll`. Lets diagnostic and leak-check tooling distinguish interceptor / post-processor / global accept-all leaks from regular handler leaks, and lets external monitors aggregate the bus's registration footprint without reflecting on internals. `MessageBus` aggregates the counters on each read by walking the per-message-type caches; consumers polling these properties in tight loops should snapshot at region boundaries.
- Runtime memory-reclamation foundations: `DxMessagingRuntimeSettings` loads from `Resources/DxMessagingRuntimeSettings` and hot-reloads eviction cadence, enablement, trim opt-out, and pool-cap changes without recreating the bus. Pooled internal collections and typed/bus slot registries preserve existing dispatch APIs while making empty handler and interceptor slots reclaimable. `IMessageBus.Trim(force)` and `MessageHandler.TrimAll(force)` reset dirty empty slots and trim shared pools on demand, `OccupiedTypeSlots` / `OccupiedTargetSlots` expose the retained bus and dirty typed-handler slot footprint for diagnostics, and idle sweeps run from emits and Unity's PlayerLoop. New user-facing reference and tuning docs ship at `docs/reference/runtime-settings.md` (per-setting reference table) and `docs/guides/memory-reclamation.md` (forced trim, idle sweep, and pool tuning narrative).
- New explicit-factory registration helpers across all three DI integrations: `VContainerRegistrationExtensions.RegisterDxMessagingBus`, `ReflexRegistrationExtensions.AddDxMessagingBus`, and `ZenjectRegistrationExtensions.BindDxMessagingBus`. Each helper exposes the bus under both the concrete `MessageBus` contract and the `IMessageBus` interface, accepts an overloadable lifetime where the container supports it, accepts a user-supplied `Func<TResolver, MessageBus>` factory, and accepts an `IDxMessagingClock` overload that constructs the bus through the new internal-only `MessageBus.CreateForInternalUse` factory so test-side clocks (for example `FakeClock`) can be injected through the container. The VContainer helper registers both contracts in one registration call, avoiding VContainer environments where chained `.AsSelf().As<IMessageBus>()` drops the concrete contract and fails with `No such registration of type: DxMessaging.Core.MessageBus.MessageBus`; the DI samples either call the helper directly or document the corresponding helper preference for their container shape.

### Changed

- Mutation tests now exercise every messaging kind (Untargeted/Targeted/Broadcast) via a single parameterized fixture (`[ValueSource(MessageScenarios.AllKinds)]`) across `MutationDuringEmissionTests`, `MutationInterceptorTests`, and `MutationDestructionTests`. Users get tighter cross-kind parity guarantees; no runtime API change. (~720 lines of duplication removed; test count preserved.)
- Renamed `UntargetedTests`, `TargetedTests`, `BroadcastTests` to `EmitUntargetedSpecificTests`, `EmitTargetedSpecificTests`, `EmitBroadcastSpecificTests` to clarify that kind-common tests live in `EmitTests` and kind-specific tests live in the renamed files. (Test-suite hardening is test-only; no `Runtime/` behavior was modified.)
- Documentation now warns up front that `MessageAwareComponent` subclasses must call `base.Awake()`, `base.OnEnable()`, `base.OnDisable()`, `base.OnDestroy()`, and `base.RegisterMessageHandlers()` from any override; admonitions added to the Quick Start, Getting Started Guide, Visual Guide, README, FAQ, and Troubleshooting pages all link to [`DXMSG006`](docs/reference/analyzers.md#dxmsg006-missing-base-call) (issue #195).

### Fixed

- Cross-priority deregistration during in-flight emit no longer drops handlers from the current dispatch.
  - Previously, when a handler at one priority removed a handler at a later priority of the same emission, the later priority's typed-handler stack was rebuilt from the now-mutated registry on first touch and the scheduled-for-removal handler was silently skipped, breaking the documented "frozen handler list per emission" contract.
  - This affected sourced-broadcast, broadcast-without-source, and targeted-without-targeting dispatch (the targeted/untargeted paths already pre-froze every bucket up-front).
  - The bus now pre-freezes every priority bucket's typed-handler caches up-front for every dispatch surface (sourced-broadcast, broadcast-without-source, targeted-without-targeting) and uses the per-emission snapshot count for the dispatch-loop early-out.
  - The sourced-broadcast and broadcast-without-source dispatch loops also no longer short-circuit on the live `cache.handlers.Count == 0` when the per-emission snapshot still holds the deregistered handler.
  - Post-processor prefreeze no longer takes a single-bucket/single-entry fast-path that skipped pre-freezing per-MessageHandler post-processor caches; a regular handler that registers a new post-processor on the same MessageHandler+priority during its own callback now sees the new post-processor on the next emission, not the in-flight one.
  - The same fix extends to cross-`MessageHandler` post-processor dispatch: the inner per-handler `RunFastHandlers` overload used by `TargetedWithoutTargeting`/`BroadcastWithoutSource` post-processors now consults the per-emission snapshot list directly instead of bailing on the live `cache.entries` count, so a sibling `MessageHandler` removing a not-yet-dispatched post-processor no longer silently skips the snapshot-pinned invocation.
  - `RegisterGlobalAcceptAll` (`HandleGlobalUntargeted`/`HandleGlobalTargeted`/`HandleGlobalBroadcast`) is intentionally NOT covered by this fix. The bus's global accept-all dispatch path prefreezes lazily per-entry inside the dispatch loop, so a sibling `MessageHandler` that removes another's global registration mid-emit causes the removed handler to be skipped on the in-flight emission. The behavior is pinned by `MutationPostProcessorAcrossHandlersTests.RemoveOtherGlobalAcceptAllAcrossHandlersDuringDispatch`; if a future change introduces upfront global-handler prefreeze, that test must be updated to expect the snapshot semantics that the per-kind paths already provide.
- `DxMessagingStaticState.Reset` is now race-safe against deferred deregistrations. Previously, when a message-aware component was destroyed but its disable callback had not yet run (Unity defers Object.Destroy to end of frame) and Reset ran in between, the deferred token teardown would log spurious "Received over-deregistration of {type} for {handler}" errors against the user's Unity console. The bus now stamps each captured deregister closure with a generation counter and silently no-ops closures captured before a Reset. Applied uniformly across every register entry point (untargeted, targeted, broadcast, GlobalAcceptAll, and all three interceptor kinds). The same race-safety guarantee is now propagated to user-installed custom global buses via `MessageBus.BumpResetGeneration()`, which `DxMessagingStaticState.Reset` invokes on the active global bus when it differs from the built-in default; the custom bus's sinks are intentionally left intact to avoid clobbering state the user installed it to preserve. User code is unaffected except that previously-spurious error logs disappear.
- `MessageRegistrationToken.RemoveRegistration(handle)` no longer leaks the staged registration entry, so a `Disable()`/`Enable()` cycle after `RemoveRegistration` no longer silently re-registers the removed handler. The fix also drops the matching metadata and call-count entries so diagnostic mode does not accumulate stale handles.
- Resolved [issue #204](https://github.com/Ambiguous-Interactive/DxMessaging/issues/204) (build artifacts and orphaned `.meta` files leaking into the npm tarball) and prevented its regression: `scripts/validate-npm-meta.js` validates real `npm pack --json --dry-run --ignore-scripts` output (and release tarballs via `--tarball`) by rejecting `bin/`, `obj/`, `*.pdb`, `*.tmp`, `*.csproj.user`, `.vs/`, `.idea/`, `*.suo`, `*.user`, and `*.DotSettings.user` paths, while enforcing `.meta` pairing for shipped Unity files and directories. The guard now runs in `validate:all`, pre-push hooks, and the release workflow.

## [2.2.0]

### Fixed

- Fixed a bug where no messages would get received by any listeners due to specifics in Unity play mode timings

## [2.1.8]

### Fixed

- Added npmignore for proper npm publishing (incorrectly packaging some items)

## [2.1.7]

### Changed

- Improved README with prominent Mental Model section
- Added Mermaid diagrams and decision flowchart for choosing message types
- Added Common Mistakes callout with troubleshooting link
- Updated performance comparison table with accurate benchmark range (10-17M ops/sec)

### Fixed

- Regenerated corrupted meta files in `scripts/wiki`

## [2.1.6]

### Added

- Concepts index page and Mental Model documentation for understanding DxMessaging's design principles

### Fixed

- Orphaned documentation pages in Concepts section now included in mkdocs.yml navigation
- Burst compiler assembly resolution errors when using DxMessaging as a package on disk and building for player platforms. Benchmarks and integration test assembly definitions now specify Editor-only platform to prevent Burst from attempting to resolve these assemblies during player builds.

## [2.1.5]

### Added

- GitHub Pages documentation deployment with MkDocs Material theme
- Wiki synchronization workflow that automatically syncs documentation to GitHub Wiki
- Documentation validation workflow that runs on pull requests and pushes
- MkDocs build validation in pre-push hooks
- Searchable documentation site at <https://ambiguous-interactive.github.io/DxMessaging/>
- Theme-aware Mermaid diagrams with automatic light/dark mode switching for GitHub Pages
- User-visible error messages when Mermaid diagrams fail to render

### Changed

- Updated `documentationUrl` in package.json to point to GitHub Pages site
- Enhanced README.md with links to documentation site, wiki, and changelog
- Mermaid diagrams now use neutral theme fallback for GitHub/VSCode markdown preview compatibility

### Fixed

- Comprehensive syntax highlighting for C# code blocks in documentation with distinct colors for keywords, types, functions, strings, numbers, comments, namespaces, and attributes
- WCAG AA accessibility compliance for code syntax highlighting in both light and dark themes
