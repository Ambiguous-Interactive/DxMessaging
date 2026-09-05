# LeakWatcher: Detecting Registration Leaks in Tests

> **One-line summary**: Any test that creates and tears down message
> registrations should bracket the work in a `LeakWatcher` to assert no
> registrations survive the watched region.

## Overview

`LeakWatcher` (`Tests/Runtime/TestUtilities/LeakWatcher.cs`) is an
`IDisposable` that snapshots every public registration counter on
`IMessageBus` at construction and asserts on `Dispose` that the counters
returned to their starting values. It is the canonical leak-detection
mechanism for the test suite; do not re-implement the counter math
inline.

The watcher reads six counters in a single pass:
`RegisteredUntargeted`, `RegisteredTargeted`, `RegisteredBroadcast`,
`RegisteredInterceptors`, `RegisteredPostProcessors`, and
`RegisteredGlobalAcceptAll`. The last three close gaps that earlier
ad-hoc leak checks missed: an interceptor that survived its register /
deregister cycle, a post-processor whose owning component was destroyed
before its handle was released, and the global-accept-all listener path
used by diagnostics.

## Public-Counter Contract

Default registration accounting is read-only and uses the six public bus
counters above. Bus slot checks use the public occupancy counters. Neither
reflects hidden bus fields. Optional handler-storage accounting uses the cold
internal query described below; it does not change the public counter contract.

If a future bus revision introduces a seventh registration kind, BOTH
`LeakWatcher.Snapshot` and `LeakWatcher.LeakedRegistrations` must be
extended in lock-step so total leak deltas remain correct. The drift is
caught by
`Tests/Runtime/Core/PublicSurfaceContractTests.cs::PublicTypeSetInDxMessagingCoreNamespaceMatchesSnapshot`,
which fails when the public type set drifts from the committed snapshot.

## Usage Patterns

The default form wraps the watched region in a `using` block. `Dispose`
calls `Assert.Fail` with a counter-by-counter diff if the region leaks;
the failure message names every initial / final pair so triage does not
require a breakpoint.

```csharp
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class LeakWatcherUsageExample : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator RegistrationDoesNotLeak(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RegistrationDoesNotLeak) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            MessageRegistrationToken token = GetToken(
                host.GetComponent<EmptyMessageAwareComponent>()
            );

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                MessageRegistrationHandle handle = ScenarioHarness
                    .RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (in SimpleUntargetedMessage _) => { }
                    );
                token.RemoveRegistration(handle);
            }

            yield break;
        }
    }
}
```

To inspect the leak count without failing the test, construct the watcher
with `throwOnLeak: false` and read `LeakedRegistrations` before disposal:

```csharp
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    internal static class LeakWatcherInspectionExample
    {
        public static int CountLeaksDuring(System.Action work)
        {
            using LeakWatcher watcher = new LeakWatcher(
                bus: MessageHandler.MessageBus,
                throwOnLeak: false,
                label: "inspection"
            );
            work();
            return watcher.LeakedRegistrations;
        }

        public static void AssertLeakRaisesOnDispose()
        {
            LeakWatcher watcher = LeakWatcher.Watch(label: "explicit");
            // ... work that intentionally leaks ...
            Assert.Throws<AssertionException>(watcher.Dispose);
        }
    }
}
```

## Optional Handler Storage Checks

Pass `handler: handler` to the `LeakWatcher` constructor to check that handler's
retained storage on the supplied bus. Use
`LeakWatcher.WatchWithSlots(bus, handler: handler)` when bus slots must also
return to baseline. Always supply the intended bus explicitly when a handler
participates in more than one bus.

The internal `MessageHandler.GetRetainedStorageCounts` query counts context keys
across typed slots and retained priority caches in both scalar and context-keyed
maps. Keys in separate typed slots count separately even when they hold the same
`InstanceId`. Several delegates sharing one priority cache count as one cache.
The query observes storage without creating it or running on the emit path.

`LeakedHandlerContexts` and `LeakedHandlerPriorityCaches` report drift from the
watcher's initial snapshot. They are separate from `LeakedRegistrations` and
bus-slot deltas: a deregistered context can retain storage while registration
counts are already back at baseline. Nonzero drift fails disposal unless
`throwOnLeak: false`. A live nonempty baseline is supported. Disposal freezes
the counts, so a later trim does not change the recorded result. Default
watchers neither traverse nor enforce handler storage.

## Cost: Capture at Region Boundaries

Both `Snapshot` and `LeakedRegistrations` walk every per-message-type
cache backing `IMessageBus.RegisteredInterceptors` and
`IMessageBus.RegisteredPostProcessors`. Each access is O(types). Snapshot
at region boundaries; do NOT read `Snapshot` inside a tight loop. The
suite's wall-clock budget is 60 s soft / 180 s hard
(`Tests/Runtime/Core/SuiteWallClockBudgetTest.cs`).

Opt-in handler accounting walks the handler's typed slots and context maps at
construction, disposal, and explicit live diagnostic reads. Use it at test-region
boundaries; never add the query to dispatch or a measured allocation window.

## Self-Tests

`Tests/Runtime/Core/LeakWatcherSelfTests.cs` parameterizes over
`MessageScenarios.AllKinds` for registration checks:

- `WatcherPassesWhenAllHandlesAreRemoved` -- a clean register / emit /
  remove cycle disposes without raising.
- `WatcherDetectsLeakedRegistrationWhenNotThrowing` -- a leaked handle
  shows up in `LeakedRegistrations` before disposal.
- `WatcherThrowsOnLeakWhenConfiguredTo` -- `Dispose` raises
  `AssertionException` when `throwOnLeak: true` and a registration is
  outstanding.

`HandlerStorageWatcherDetectsDeferredContextAfterRegistrationsDrain` covers
targeted and broadcast cleanup with throwing and non-throwing watchers. It
proves a default watcher accepts drained registration counts while the optional
handler check detects retained storage, and that trim restores the baseline.
`HandlerStorageCountsRetainedPrioritiesAndOnlyTheRequestedBus` covers all three
kinds, shared priorities, multiple message types, simultaneous handler and
postprocessor slots, an unused bus, and isolation between two buses.

## Adding a New Counter

When the bus grows a new public registration counter:

1. Extend `IMessageBus` with the new property; add it to
   `Tests/Runtime/Core/Snapshots/public-surface.txt` (the committed
   snapshot consumed by `PublicSurfaceContractTests`).
1. Add the counter to `LeakWatcher` in three places: `_initialXxx` /
   `_finalXxx` fields, the `Snapshot` sum, and the `TotalDelta` parameter
   list. Extend the failure-message format string so leak diagnostics
   include the new pair.
1. Add a test row in `LeakWatcherSelfTests` exercising the new counter.
1. Update this skill's "Public-Counter Contract" section.

A skipped watcher extension under-counts silently; the public-surface
snapshot test catches the drift first, and a self-test that registers
exclusively against the new counter fails loudly otherwise.

## When NOT to Use

- Inside a tight loop. Use one watcher around the loop body, not one per
  iteration.
- For unrelated resources. The watcher covers bus accounting and optional
  typed-handler storage; GameObject and NativeArray leaks are out of scope.
- For benchmark hot paths. Allocation / Performance fixtures avoid the
  watcher because the per-call O(types) cost shows up in measurements.

## See Also

- [Lifecycle Edge-Case Test Coverage](../../test-coverage-design/references/lifecycle-edge-coverage.md)
- [Tests Must Be Parameterized by Message Kind](../../test-coverage-design/references/tests-must-be-parameterized-by-message-kind.md)
- [Test Coverage Requirements](../../test-coverage-design/references/comprehensive-test-coverage.md)
- [MessageAwareComponent Base-Call Contract](../../unity-editor-conventions/references/base-call-contract.md)

## References

- NUnit `IDisposable` cleanup pattern: https://docs.nunit.org/articles/nunit/writing-tests/attributes/teardown.html
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@latest

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-02 | Initial version |
