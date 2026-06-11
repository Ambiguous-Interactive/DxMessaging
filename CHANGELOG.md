# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Untargeted dispatch now resolves handlers to flat, pooled delegate arrays
  at snapshot-build time instead of walking per-handler dictionaries and
  dispatch links per message. Measured on Editor PlayMode Mono x64: one
  handler 17.5M to 27.9M emits/sec, four handlers at one priority 3.9M to
  24.1M, four post-processors 3.3M to 14.6M, all with zero steady-state
  allocations and unchanged dispatch semantics (snapshot freezing, priority
  and registration order, mid-emission registration visibility, immediate
  deactivation). Targeted and broadcast dispatch are unchanged and will be
  migrated next.

### Fixed

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
- Unity projects no longer keep stale DxMessaging analyzer `-a:` entries in `csc.rsp`; the setup script removes package-cache analyzer registrations so Unity loads the shipped analyzer and source generator once through the `RoslynAnalyzer`-labeled plugin copy.
- Provider-backed emit helpers now route sourced, targeted, and untargeted messages through the resolved `IMessageBus`, so custom bus and DI-provider callers no longer fall back to the global bus for interface-shaped message dispatch.
- Standalone and IL2CPP player builds now compile. The dispatch hot path performs reinterpret casts that previously used `System.Runtime.CompilerServices.Unsafe`; the Unity Editor supplies that type, but player builds under the .NET Standard 2.0 profile do not, so editmode and playmode passed while standalone IL2CPP failed to build with `CS0103: The name 'Unsafe' does not exist in the current context`. Those calls now route through Unity's built-in `UnsafeUtility` (in `UnityEngine.CoreModule`), which resolves identically in the Editor and every player scripting backend. The change preserves the existing zero-allocation dispatch behavior and adds no package dependency or shipped assembly.
- Shipped source generators now compile against Unity 2021-compatible Roslyn 3.8 APIs and use the classic `ISourceGenerator` entry point, preventing Unity 2021.3 analyzer-host load failures (`CS8032`) while preserving the generated message-id and auto-constructor output for newer Unity editors.
- Unity CI editor provisioning now self-heals managed partial or manually copied installs through explicit provisioning profiles: edit/play/release/benchmark jobs verify only the editor, standalone jobs verify `windows-il2cpp`, Android tooling is opt-in, and missing profile-required module groups trigger quarantine plus fresh Unity CLI reinstall instead of failing later in setup.
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
