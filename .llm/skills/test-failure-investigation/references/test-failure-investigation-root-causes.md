# Test Failure Root Causes and Anti-Patterns

> **One-line summary**: Identify common root causes of flaky tests and avoid anti-patterns that hide failures.

## Overview

Most flaky failures fall into a few categories. This guide helps you recognize the pattern and apply the right fix.

## Problem Statement

Treating symptoms instead of causes leads to recurring failures and unreliable CI results.

## Solution

### Timing and Race Conditions

**Symptom**: Test passes locally, fails in CI.

**Investigation**:

- Look for async operations without synchronization
- Check callbacks that may fire before assertions
- Inspect thread safety of shared resources

**Fix**:

```csharp
// BAD: Race between Send and assertion
bus.SendAsync(message);
Assert.IsTrue(handler.Received);

// GOOD: Proper synchronization
bus.Send(message);
Assert.IsTrue(handler.Received);
```

### Shared State Between Tests

**Symptom**: Test passes alone, fails with others.

**Fix**:

```csharp
public class MessageBusTests
{
    private MessageBus _bus;

    [SetUp]
    public void SetUp()
    {
        _bus = new MessageBus();
    }

    [TearDown]
    public void TearDown()
    {
        _bus.Dispose();
    }
}
```

### Order Dependencies

**Symptom**: Tests fail when executed in a different order.

**Fix**: Ensure each test is fully self-contained with no shared static state.

### Unity-Specific Issues

**Symptom**: Test fails only in PlayMode or only in EditMode.

**Fix**:

```csharp
[UnityTest]
public IEnumerator ComponentInitializesCorrectly()
{
    GameObject go = new GameObject("Test");
    TestComponent component = go.AddComponent<TestComponent>();

    yield return null;

    Assert.IsTrue(component.IsInitialized,
        "Component should be initialized after Start()");

    Object.Destroy(go);
}
```

## Anti-Patterns to Avoid

| Anti-Pattern                                 | Why It's Bad                       | What To Do Instead              |
| -------------------------------------------- | ---------------------------------- | ------------------------------- |
| `Thread.Sleep()` / `WaitForSeconds()` as fix | Masks timing bugs, slows tests     | Fix the race condition properly |
| `[Ignore]` attribute                         | Hides real bugs                    | Fix or remove the test          |
| Retry loops                                  | Masks intermittent failures        | Find and fix root cause         |
| `try/catch` swallowing                       | Hides exceptions                   | Let exceptions propagate        |
| "Works on my machine"                        | Different environments expose bugs | Fix for all environments        |
| Increasing timeouts                          | Slows test suite, masks issues     | Fix the underlying timing       |
| Deleting failing tests                       | Loses coverage                     | Fix the test                    |

## Investigation Checklist

- [ ] Can I reproduce the failure reliably?
- [ ] Do I understand the behavior the test verifies?
- [ ] Have I inspected actual vs expected values?
- [ ] Have I read the production code path being tested?
- [ ] Is this a production bug or a test bug?
- [ ] Are there shared state or order dependencies?
- [ ] Are there timing/race issues?
- [ ] Does the fix address the root cause?
- [ ] Have I run the test multiple times after fixing?

## See Also

- [Test Failure Investigation](./test-failure-investigation.md)
- [Investigation Procedure](./test-failure-investigation-procedure.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
