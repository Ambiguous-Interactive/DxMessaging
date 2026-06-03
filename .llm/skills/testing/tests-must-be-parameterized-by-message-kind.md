---
title: "Tests Must Be Parameterized by Message Kind"
id: "tests-must-be-parameterized-by-message-kind"
category: "testing"
version: "1.0.0"
created: "2026-05-01"
updated: "2026-05-01"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/TestUtilities/MessageScenario.cs"
    - path: "Tests/Runtime/TestUtilities/MessageScenarios.cs"
    - path: "Tests/Runtime/TestUtilities/ScenarioHarness.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "data-driven"
  - "parameterization"
  - "messaging"
  - "scenarios"
  - "unity"
  - "nunit"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of NUnit ValueSource and the project's MessageScenario harness."

impact:
  performance:
    rating: "none"
    details: "Pattern affects test maintainability, not runtime performance."
  maintainability:
    rating: "critical"
    details: "Eliminates test duplication; adding a kind requires no triplet-rewrite."
  testability:
    rating: "critical"
    details: "Improves coverage parity across message kinds."

prerequisites:
  - "data-driven-tests"
  - "comprehensive-test-coverage"

dependencies:
  packages: []
  skills:
    - "data-driven-tests"
    - "comprehensive-test-coverage"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "MessageScenario parameterization"
  - "Triplet test consolidation"

related:
  - "data-driven-tests"
  - "comprehensive-test-coverage"
  - "test-coverage-data-driven"
  - "shared-test-fixtures"

status: "stable"
---

# Tests Must Be Parameterized by Message Kind

> **One-line summary**: Any test that exercises DxMessaging dispatch across more
> than one of `Untargeted`, `Targeted`, or `Broadcast` must be a single
> parameterized method driven by `MessageScenario`, not three near-identical
> triplets.

## Overview

DxMessaging exposes three dispatch kinds: untargeted, targeted, and broadcast.
Historically the test suite shipped a triplet of test methods for every
behavior - one per kind - copy-pasted into ~720 lines of duplicated assertions
with subtly different formatting. Adding a new shared behavior meant writing
the same body three times; fixing a bug meant fixing it three times; missing
the third copy was a routine source of coverage drift.

The project now ships a parameterized scenario harness
(`Tests/Runtime/TestUtilities/`) that lets a single test method cover all
kinds. The contract is enforced by
`TestAttributeContractTests.EveryEmitTestUsesScenarioParameterization` so a
regression cannot land silently.

## Problem Statement

Triplet tests are easy to write and easy to drift:

```csharp
[UnityTest]
public IEnumerator HandlerReceivesEmittedUntargetedMessage()
{
    // ... 40 lines, untargeted ...
}

[UnityTest]
public IEnumerator HandlerReceivesEmittedTargetedMessage()
{
    // ... 40 lines, targeted (slightly different) ...
}

[UnityTest]
public IEnumerator HandlerReceivesEmittedBroadcastMessage()
{
    // ... 40 lines, broadcast (slightly different again) ...
}
```

Three signatures, three bodies, one behavior. Every fix has to land three
times. Coverage parity is a manual review item.

## Solution

Replace the triplet with one method that takes a `MessageScenario` from
`MessageScenarios.AllKinds` via NUnit `[ValueSource]`, and uses
`ScenarioHarness` to pick the right register / emit overload.

```csharp
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class EmitTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator HandlerReceivesEmittedMessage(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerReceivesEmittedMessage) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component =
                host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _ = ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario, token, (ref SimpleUntargetedMessage _) => ++count);
                    SimpleUntargetedMessage u = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref u);
                    break;
                case MessageKind.Targeted:
                    _ = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario, token, component,
                        (ref SimpleTargetedMessage _) => ++count);
                    SimpleTargetedMessage t = new();
                    ScenarioHarness.EmitTargeted(scenario, ref t, component);
                    break;
                case MessageKind.Broadcast:
                    _ = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario, token, component,
                        (ref SimpleBroadcastMessage _) => ++count);
                    SimpleBroadcastMessage b = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref b, component);
                    break;
            }

            Assert.AreEqual(1, count, $"Scenario {scenario} should dispatch exactly once.");
            yield return null;
        }
    }
}
```

NUnit produces three discovered tests - `HandlerReceivesEmittedMessage(Untargeted)`,
`HandlerReceivesEmittedMessage(Targeted)`, `HandlerReceivesEmittedMessage(Broadcast)` -
from one source method. Adding a fourth kind means adding one entry to
`MessageScenarios.AllKinds`; the test does not change.

## Exception: Kind-Specific Fixtures

Some assertions are intrinsically kind-specific. Untargeted dispatch fans out to
every registered handler regardless of receiver identity; targeted and broadcast
do not. The fan-out shape, the empty-target case, and the broadcast-from-source
routing all have semantics that do not translate cleanly to the other kinds.

Those tests live in fixtures whose names match `*Specific*Tests`:

- `EmitUntargetedSpecificTests`
- `EmitTargetedSpecificTests`
- `EmitBroadcastSpecificTests`

The contract test exempts any fixture matching that pattern. Tests that DO
generalize across kinds belong in `EmitTests` (or another non-`*Specific*`
fixture) and MUST be parameterized.

## Enforcement

`Tests/Runtime/Core/TestAttributeContractTests.cs` contains
`EveryEmitTestUsesScenarioParameterization`. The test reflects over every
`[UnityTest]` method in the `DxMessaging.Tests.Runtime` namespace, ignores any
fixture whose name ends with `Tests` and contains `Specific`, and fails the
build for any remaining method whose name mentions `Untargeted`, `Targeted`,
or `Broadcast` but whose parameter list does not include `MessageScenario`.

If a new test triplet sneaks in, CI fails with a pointer back to this skill.

## See Also

- [Data-Driven Tests](data-driven-tests.md)
- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Data-Driven Coverage Patterns](test-coverage-data-driven.md)
- [Shared Test Fixtures](shared-test-fixtures.md)
- [Allocation Coverage Required for Dispatch](allocation-coverage-required-for-dispatch.md)
- [Single Thread Contract](single-thread-contract.md)
- [Lifecycle Edge-Case Test Coverage](lifecycle-edge-coverage.md)
- [LeakWatcher: Detecting Registration Leaks in Tests](leak-watcher-usage.md)

## References

- NUnit `ValueSource` documentation: https://docs.nunit.org/articles/nunit/writing-tests/attributes/valuesource.html
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@latest

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-01 | Initial version |
