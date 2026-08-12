# Session 197 - Copy-Safe Zero-GC Disposal

Date: 2026-08-12
Branch: `codex/session-197-copy-safe-disposal`
Issues: #375 copy-safe disposal and #354 docs-test SDK availability

## Outcome

The two public disposable structs now remain safe when copied without allocating managed objects
per operation while established slot capacity is reused:

- `GlobalMessageBusScope` stores a versioned index into a segmented, reusable process-wide slot
  table. Copies share the authoritative slot state. Nested scopes restore the nearest active parent
  in either disposal order, explicit global-bus changes invalidate prior scopes, and stale copies
  cannot affect a scope that later recycles the same slot.
- `RegistrationDisposable` is a stateless readonly wrapper over the token and opaque registration
  handle. The token's slot and monotonic identity validation makes every successful removal have
  effect once, while a failed deregistration remains retryable through any copy.
- Registration handle identities no longer rewind during `DxMessagingStaticState.Reset`, preventing
  a pre-reset stale wrapper from colliding with a post-reset registration that reused its arena slot.

The global override table starts with 1,024 slots and grows in fixed blocks when a new peak crosses
established capacity, matching the segmented-table pattern used by UnityHelpers. Existing blocks
never move. Out-of-order-disposed ancestors retain their slots until all newer scopes have ended.
Growth beyond the first block, unwind, and reuse are covered and documented. Concrete scope use is
zero-GC while established capacity is reused; a new peak allocates its block and may grow the small
block directory. Conversion to `IDisposable` boxes the value. Dispatch still reads the direct global
bus field and gained no per-emit work.

## Disposable Sweep

The repository contains exactly two public stateful/lifecycle disposable structs, both addressed
above. The public `MessageCache<T>.MessageCacheEnumerator`, internal `CyclicBuffer<T>` enumerator,
and internal `RegistrationMetadataView.Enumerator` also implement `IDisposable`, but their disposal
methods are intentional no-ops with no shared state to corrupt when copied. The private
`MessageBus.DispatchLease` remains unchanged: all three internal call sites create and consume it
lexically in `using` statements on the dispatch hot path, with no copy or escape. Adding external
generation state there would add work to every emit without fixing a reachable defect.

`DxMessagingRuntimeSettingsProvider.OverrideToken` is a sealed reference token rather than a
copyable struct, but the sweep found a separate out-of-order nested restoration defect. Issue #384
records the reproduction, allocation/API constraints, and acceptance tests for a focused fix.

## Allocation Evidence

The allocation matrix now proves zero `GC.Alloc` activity after warmup for:

- balanced global override creation and disposal;
- nested slot acquisition, out-of-order unwind, and explicit invalidation;
- repeated reuse after growing through three 1,024-slot blocks;
- concrete `AsDisposable(handle).Dispose()` forwarding.

Closures, buses, tokens, and registrations are constructed outside measured windows. Independent
allocation review confirmed the measured paths contain no boxing, reference construction, delegate
creation, LINQ, collection growth, or dispatch-path change. Fixed-block growth occurs only when a
new peak crosses established capacity and is explicitly outside the steady-state contract.

## CI Improvement

`.docs-tests/global.json` now requests the first .NET 9 feature band (`9.0.100`) and uses
`latestFeature`, allowing the docs tests to run with any installed stable .NET 9 feature band. A
configuration contract guards that policy. The source-generator SDK stays exactly pinned because
it protects the shipped analyzer payload.

## Unity Evidence

The shared Unity 6000.4.6f1 editor was refreshed only after a clean, stopped, main-stage preflight.
Fresh assembly reflection proved the new runtime and allocation tests were loaded.

- Full PlayMode suite before final review fixes: 997 passed, 0 failed, 1 skipped in 25.44 seconds.
- Full EditMode main and allocation suites before final review fixes: 915 passed, 0 failed, 1
  skipped, 8 inconclusive in 294.02 seconds.
- Post-review focused runtime fixtures: 76 passed, 0 failed in 0.24 seconds.
- Post-review focused allocation rows: 3 passed, 0 failed in 3.85 seconds.
- Final post-cap-removal global override fixture: 26 passed, 0 failed in 0.16 seconds.
- Final post-cap-removal allocation rows: 4 passed, 0 failed in 4.07 seconds.

The editor ended stopped, idle, outside prefab mode, with its open scene clean.

## Local Evidence

- Documentation compilation/configuration tests: 583 passed, 0 failed.
- Source-generator/analyzer tests: 246 passed, 0 failed, with analyzer payload copying disabled.
- Script tests: 409 passed, 0 failed.
- Full repository validation: passed, including package and analyzer reproducibility.
- CSharpier, Prettier, Markdown lint, spelling, and `git diff --check`: passed.
- The parent product-change `master` commit's Unity and performance runs completed successfully;
  the later performance-docs update on current `master` also passed its complete static check set.

## Review

Three independent passes reviewed correctness, test coverage, and allocation behavior. Their findings
added true generation-recycling coverage, growth beyond the initial block and tombstone recovery,
both failed-disposal retry orders, default/null cases, broader allocation branches, SDK floor
correction, boxing and single-thread documentation, and removal of the initial fixed 1,024 nesting
cap. Final re-review reported no actionable production, allocation, test, or documentation finding.

## Pull Request

Draft PR #385 contains the implementation and its evidence:
https://github.com/Ambiguous-Interactive/DxMessaging/pull/385
