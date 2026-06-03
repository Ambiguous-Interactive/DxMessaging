---
title: "LeakWatcher: Detecting Registration Leaks in Tests"
id: "leak-watcher-usage"
category: "testing"
version: "1.0.0"
created: "2026-05-02"
updated: "2026-05-02"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/TestUtilities/LeakWatcher.cs"
    - path: "Tests/Runtime/Core/LeakWatcherSelfTests.cs"
    - path: "Runtime/Core/MessageBus/IMessageBus.cs"
    - path: "Tests/Runtime/Core/PublicSurfaceContractTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "leaks"
  - "registration"
  - "lifecycle"

complexity:
  level: "basic"
  reasoning: "Wraps a small IDisposable around the public IMessageBus counters; usage is mechanical."

impact:
  performance:
    rating: "low"
    details: "Each counter snapshot is O(types) due to per-message-type interceptor / post-processor caches; restrict to region boundaries."
  maintainability:
    rating: "high"
    details: "Centralizes leak-detection in one utility so test fixtures stop rolling their own."
  testability:
    rating: "high"
    details: "Brings the bus's public counter surface under test discipline."

prerequisites:
  - "lifecycle-edge-coverage"

dependencies:
  packages: []
  skills:
    - "lifecycle-edge-coverage"
    - "tests-must-be-parameterized-by-message-kind"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "Leak watcher"
  - "Registration leak detection"

related:
  - "lifecycle-edge-coverage"
  - "base-call-contract"
  - "comprehensive-test-coverage"
  - "tests-must-be-parameterized-by-message-kind"

status: "stable"
---

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

The watcher is read-only and goes through public surface exclusively. The
counter set above IS the canonical leak-detection surface; no hidden field
of the bus is reflected. The "counter source" doc-comment block on
`LeakWatcher` documents this contract verbatim.

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
                        (ref SimpleUntargetedMessage _) => { }
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

## Cost: O(types) per Snapshot

Both `Snapshot` and `LeakedRegistrations` walk every per-message-type
cache backing `IMessageBus.RegisteredInterceptors` and
`IMessageBus.RegisteredPostProcessors`. Each access is O(types). Snapshot
at region boundaries; do NOT read `Snapshot` inside a tight loop. The
suite's wall-clock budget is 60 s soft / 180 s hard
(`Tests/Runtime/Core/SuiteWallClockBudgetTest.cs`).

## Self-Tests

`Tests/Runtime/Core/LeakWatcherSelfTests.cs` parameterizes over
`MessageScenarios.AllKinds` and exercises three behaviors:

- `WatcherPassesWhenAllHandlesAreRemoved` -- a clean register / emit /
  remove cycle disposes without raising.
- `WatcherDetectsLeakedRegistrationWhenNotThrowing` -- a leaked handle
  shows up in `LeakedRegistrations` before disposal.
- `WatcherThrowsOnLeakWhenConfiguredTo` -- `Dispose` raises
  `AssertionException` when `throwOnLeak: true` and a registration is
  outstanding.

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
- For non-bus resources. The watcher reads `IMessageBus` only; GameObject
  leaks and NativeArray leaks are out of scope.
- For benchmark hot paths. Allocation / Performance fixtures avoid the
  watcher because the per-call O(types) cost shows up in measurements.

## See Also

- [Lifecycle Edge-Case Test Coverage](./lifecycle-edge-coverage.md)
- [Tests Must Be Parameterized by Message Kind](./tests-must-be-parameterized-by-message-kind.md)
- [Test Coverage Requirements](./comprehensive-test-coverage.md)
- [MessageAwareComponent Base-Call Contract](../unity/base-call-contract.md)

## References

- NUnit `IDisposable` cleanup pattern: https://docs.nunit.org/articles/nunit/writing-tests/attributes/teardown.html
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@latest

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-02 | Initial version |
