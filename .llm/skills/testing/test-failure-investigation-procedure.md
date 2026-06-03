---
title: "Test Failure Investigation Procedure"
id: "test-failure-investigation-procedure"
category: "testing"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "investigation"
  - "procedure"
  - "debugging"

complexity:
  level: "intermediate"
  reasoning: "Requires disciplined investigation and data gathering"

impact:
  performance:
    rating: "none"
    details: "Investigation process; no runtime impact"
  maintainability:
    rating: "critical"
    details: "Systematic investigation prevents regressions"
  testability:
    rating: "critical"
    details: "Keeps test suite reliable"

prerequisites:
  - "Understanding of debugging techniques"

dependencies:
  packages: []
  skills:
    - "test-failure-investigation"
    - "test-diagnostics"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "Test investigation steps"

related:
  - "test-failure-investigation"
  - "test-failure-investigation-root-causes"
  - "test-diagnostics"

status: "stable"
---

# Test Failure Investigation Procedure

> **One-line summary**: Follow a repeatable investigation process before making any fix.

## Overview

A structured process ensures you identify the real root cause rather than masking symptoms.

## Problem Statement

Ad-hoc fixes often hide underlying issues and produce flaky tests.

## Solution

### Step 1: Reproduce the Failure

Ensure the failure can be reproduced reliably.

```csharp
// Run the specific failing test multiple times
// Unity: Window > Test Runner > Right-click test > Run
// CLI: dotnet test --filter "FullyQualifiedName=Namespace.TestClass.TestMethod"

for i in {1..100}; do
    echo "Run $i"
    dotnet test --filter "TestName" || echo "FAILED on run $i"
done
```

### Step 2: Understand the Expected Behavior

Document what the test is verifying:

1. What production behavior is being tested?
1. What are the preconditions?
1. What is the expected outcome?
1. Why does this matter to users?

### Step 3: Analyze the Failure

Add diagnostic output to capture actual state at failure.

```csharp
[Test]
public void MessageRoutedToCorrectHandler()
{
    MessageBus bus = new MessageBus();
    TestHandler handler1 = new TestHandler();
    TestHandler handler2 = new TestHandler();

    bus.RegisterTargeted<TestMessage>(handler1, targetId1);
    bus.RegisterTargeted<TestMessage>(handler2, targetId2);

    bus.SendTargeted(targetId1, new TestMessage());

    UnityEngine.Debug.Log($"Handler1 received: {handler1.ReceivedCount}, Handler2 received: {handler2.ReceivedCount}");
    UnityEngine.Debug.Log($"Target1: {targetId1}, Target2: {targetId2}");
    UnityEngine.Debug.Log($"Bus registration count: {bus.GetRegistrationCount()}");

    Assert.AreEqual(1, handler1.ReceivedCount,
        $"Handler1 should receive exactly 1 message. Target1={targetId1}, Handler1Id={handler1.Id}");
    Assert.AreEqual(0, handler2.ReceivedCount,
        $"Handler2 should receive no messages. Target2={targetId2}, Handler2Id={handler2.Id}");
}
```

### Step 4: Investigate Production Code

Read the code path the test exercises:

- Check initialization order and assumptions
- Inspect thread-safety and shared state
- Identify edge cases the test might trigger

### Step 5: Identify Root Cause

Categorize the failure:

| Category              | Description                         | Example                           |
| --------------------- | ----------------------------------- | --------------------------------- |
| **Production Bug**    | Code doesn't behave as intended     | Race condition in message routing |
| **Test Bug**          | Test has incorrect expectations     | Wrong expected value              |
| **Test Setup Bug**    | Test doesn't set up state correctly | Missing initialization            |
| **Order Dependency**  | Test depends on execution order     | Shared static state               |
| **Timing Issue**      | Test has race conditions            | Async operation not awaited       |
| **Environment Issue** | Test depends on environment         | File paths, time zones            |

### Step 6: Fix the Root Cause

Fix the actual problem, not the symptom:

```csharp
// Fix production code for race conditions
public void Register(IHandler handler)
{
    lock (_registrationLock)
    {
        _handlers.Add(handler);
    }
}

// Fix test expectation after investigation
[Test]
public void UnregisteredHandlerReceivesNoMessages()
{
    handler.Unregister();
    bus.Send(new TestMessage());

    Assert.AreEqual(0, handler.ReceivedCount,
        "Handler should receive no messages after Unregister() returns");
}
```

### Step 7: Verify the Fix

- Run the fixed test repeatedly (10x or more)
- Run related tests to ensure no regressions
- Run the full suite before committing

```bash
for i in {1..50}; do dotnet test --filter "TestName" || exit 1; done
```

### Step 8: Document Findings

Record edge cases or behavior clarifications in code comments or docs.

```csharp
/// <remarks>
/// Investigation (2026-01-22): This test was failing intermittently due to
/// a race condition in handler registration. The fix ensures registration
/// is atomic. Message delivery order now matches registration order.
/// </remarks>
[Test]
public void HandlersReceiveMessagesInRegistrationOrder()
{
    // ...
}
```

## See Also

- [Test Failure Investigation](test-failure-investigation.md)
- [Root Causes and Anti-Patterns](test-failure-investigation-root-causes.md)
- [Test Diagnostics](test-diagnostics.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
