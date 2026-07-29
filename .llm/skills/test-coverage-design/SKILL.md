---
name: test-coverage-design
description: "What a change must be covered by before it lands: happy-path, negative, edge, unexpected, and defensive scenario categories; the canonical DxMessaging lifecycle edge-case set (scene unload mid-dispatch, DontDestroyOnLoad, prefab pooling churn, token disable/re-enable, post-Reset emit, OnApplicationQuit, cross-kind reentrancy); the MessageScenario parameterization rule that bans Untargeted/Targeted/Broadcast test triplets; and Unity test naming, assertion, and anti-pattern rules. Use when writing tests for a new feature or bug fix, when touching the bus dispatch path, or when a reviewer asks whether coverage is sufficient."
metadata:
  category: "testing"
  tags: "testing, coverage, edge-cases, data-driven, unity, nunit, best-practices, quality"
---

# Test Coverage Design

Every feature, fix, and dispatch-path change must arrive with tests that span the full scenario matrix, cover the canonical lifecycle edge cases, and are parameterized by message kind rather than copy-pasted per kind.

## When to use

- Adding a public API, fixing a bug, optimizing, or refactoring, and deciding what tests are required.
- Changing registration, emission, deregistration, or the interceptor / post-processor pipeline.
- Writing a test that mentions `Untargeted`, `Targeted`, or `Broadcast`.
- Adding a scene-load, pooling, destruction, or reentrancy test.
- Reviewing a PR for coverage gaps, weak assertions, or Unity test anti-patterns.

## Rules

### Coverage is required, per change type

| Change           | Required tests                                                  |
| ---------------- | --------------------------------------------------------------- |
| New feature      | Tests for every public API surface it adds                      |
| Bug fix          | A regression test that fails before the fix and passes after    |
| Performance work | Benchmark tests proving the improvement                         |
| Refactor         | Existing tests pass; add tests where the refactor exposes a gap |
| API change       | Updated existing tests plus tests for the new behavior          |

Cover all five scenario categories, not just the first: normal / happy path, negative (error conditions), edge (boundaries such as `0`, `int.MaxValue`, `int.MinValue`, empty, null), unexpected usage (duplicate registration, out-of-order calls), and defensive "impossible" cases (a handler that throws mid-dispatch).

### Parameterize by message kind - never write triplets

- A test that exercises more than one dispatch kind MUST be a single method taking a `MessageScenario` via `[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]`, using `ScenarioHarness.RegisterUntargeted` / `RegisterTargeted` / `RegisterBroadcast` and the matching `Emit*` overloads. NUnit expands one source method into three discovered tests.
- Genuinely kind-specific assertions (untargeted fan-out shape, empty-target routing, broadcast-from-source) live in fixtures named `*Specific*Tests`: `EmitUntargetedSpecificTests`, `EmitTargetedSpecificTests`, `EmitBroadcastSpecificTests`. Everything that generalizes belongs in `EmitTests` or another non-`*Specific*` fixture.
- Enforced by `TestAttributeContractTests.EveryEmitTestUsesScenarioParameterization`, which fails any `[UnityTest]` in `DxMessaging.Tests.Runtime` whose name mentions a kind but whose parameter list has no `MessageScenario`.

### The canonical lifecycle edge-case set

Every dispatch-path change must keep these green, and must add a row when it introduces an uncovered mechanism. `Tests/Runtime/Core/LifecycleEdgeCasesTests.cs` pins scene unload mid-dispatch, DontDestroyOnLoad scene transitions, registration from a `sceneLoaded` callback, 100-cycle prefab pooling churn, token disable and re-enable mid-dispatch, empty-bus emit, post-`Reset` emit, `OnApplicationQuit` drain, and host destroy mid-dispatch. `Tests/Runtime/Core/ReentrantEmissionExtendedTests.cs` pins all six cross-kind reentrancy permutations, 10-level deep recursion with the `IMessageBus.EmissionId` invariant, reentrant unsubscribe-then-resubscribe, nested throw, interceptor veto, and interceptor mutation on re-emit.

When adding an edge case:

- Drive it from `MessageScenarios.AllKinds`.
- Track every spawned `GameObject` with `_spawned.Add(host)`; pinned by `TestAttributeContractTests.FixturesUsingMessagingTestBaseUseSpawnedCleanupPattern`.
- Gate scene load/unload tests behind `[Category("UnityRuntime")]`.
- Wrap register/teardown regions in `using (LeakWatcher.Watch(...))`.
- Format assertion messages with a leading `[{0}]` keyed on `scenario.Kind` so a per-kind regression is triageable.

Gotchas that have bitten real PRs: build transient scenes with `SceneManager.CreateScene` (never assume Build Settings has one) and yield a frame before moving objects into them; place `LogAssert.Expect` BEFORE the triggering action; keep default-suite iteration counts under 100 and push heavier counts behind `[Category("Stress")]` or `[Category("Allocation")]`; use `DxMessagingStaticState.Reset` as the only way to clear the global bus mid-test.

### Unity test mechanics

- `[Test]` for pure logic, `[UnityTest]` returning `IEnumerator` when a frame is genuinely needed. `async Task` test methods are not supported.
- Unity overrides `==`, so assert `component == null, Is.True` rather than `Assert.That(component, Is.Null)`.
- A `MessageRegistrationToken` test setup needs all three: `MessageHandler { active = true }`, an explicit local `MessageBus` (never the global one), and `token.Enable()` after `MessageRegistrationToken.Create`.

### Naming, structure, assertions

- Fixture names are `{ClassUnderTest}Tests` or `{Feature}Tests`; method names are PascalCase sentences with no underscores. The `fix-csharp-underscore-methods` pre-commit hook auto-converts offenders; validate locally with `node scripts/fix-csharp-underscore-methods.js --check --all`. Avoid `git commit --no-verify`.
- Every assertion carries a failure message naming the expected and actual values. Use `CollectionAssert` for collections and assert one concept per test.
- Banned in test code: `#region`, `var`, `[Ignore]`, `[Description]`, and tests with no assertions.

## References

| Document                                                                                                      | Purpose                                                                                                       |
| ------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| [comprehensive-test-coverage.md](./references/comprehensive-test-coverage.md)                                 | The per-change-type coverage requirement table and the five scenario categories.                              |
| [lifecycle-edge-coverage.md](./references/lifecycle-edge-coverage.md)                                         | The canonical lifecycle/reentrancy scenario table, how to add an entry, and the gotchas each entry came from. |
| [test-coverage-data-driven.md](./references/test-coverage-data-driven.md)                                     | Worked `[TestCase]` and `[TestCaseSource]` patterns for expanding coverage without duplication.               |
| [test-coverage-organization-assertions.md](./references/test-coverage-organization-assertions.md)             | Fixture and method naming, SetUp/TearDown structure, and expressive assertion forms.                          |
| [test-coverage-scenario-categories.md](./references/test-coverage-scenario-categories.md)                     | Code examples for each of the five scenario categories against the message bus.                               |
| [test-coverage-unity-anti-patterns.md](./references/test-coverage-unity-anti-patterns.md)                     | Unity Test Framework constraints, token setup requirements, banned constructs, and the review checklist.      |
| [tests-must-be-parameterized-by-message-kind.md](./references/tests-must-be-parameterized-by-message-kind.md) | The MessageScenario parameterization rule, the `*Specific*Tests` exemption, and its contract test.            |
