---
title: "Unity Test Considerations and Anti-Patterns"
id: "test-coverage-unity-anti-patterns"
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
  - "unity"
  - "anti-patterns"
  - "nunit"

complexity:
  level: "basic"
  reasoning: "Applies Unity-specific test constraints and common pitfalls"

impact:
  performance:
    rating: "none"
    details: "Testing guidance only"
  maintainability:
    rating: "high"
    details: "Avoids invalid or flaky Unity tests"
  testability:
    rating: "high"
    details: "Ensures tests align with Unity Test Framework limitations"

prerequisites:
  - "Understanding of Unity Test Framework"

dependencies:
  packages: []
  skills:
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
  - "Unity test rules"

related:
  - "comprehensive-test-coverage"
  - "test-coverage-organization-assertions"

status: "stable"
---

# Unity Test Considerations and Anti-Patterns

> **One-line summary**: Follow Unity-specific testing constraints and avoid common anti-patterns.

## Overview

Unity Test Framework has specific rules: use `[UnityTest]` for coroutine-based tests, avoid `async Task`, and handle `UnityEngine.Object` null checks correctly.

## Problem Statement

Tests that violate Unity constraints fail unpredictably or provide false results.

## Solution

### Test vs UnityTest

```csharp
[Test]
public void PureLogicTestWithNoUnityDependencies()
{
    MessageBus bus = new MessageBus();
    Assert.That(bus.HandlerCount, Is.EqualTo(0));
}

[UnityTest]
public IEnumerator ComponentRegistersOnStart()
{
    GameObject go = new GameObject("Test", typeof(MessageAwareComponent));

    yield return null;

    MessageAwareComponent component = go.GetComponent<MessageAwareComponent>();
    Assert.That(component.IsRegistered, Is.True);

    Object.DestroyImmediate(go);
}
```

### No Async Task Tests

```csharp
// BAD: Unity doesn't support async Task tests
[Test]
public async Task AsyncTestMethod()
{
    await SomeAsyncOperation();
}

// GOOD: IEnumerator with UnityTest
[UnityTest]
public IEnumerator OperationCompletesAfterDelay()
{
    AsyncOperation operation = StartAsyncOperation();

    while (!operation.IsComplete)
    {
        yield return null;
    }

    Assert.That(operation.Result, Is.Not.Null);
}
```

### Testing Exception Throwing

```csharp
[Test]
public void ThrowsOnInvalidInput()
{
    Assert.Throws<ArgumentNullException>(
        () => _messageBus.Register(null),
        "Should throw ArgumentNullException for null handler");
}
```

### Proper UnityEngine.Object Null Checks

```csharp
// BAD: Unity overrides == semantics
Assert.That(component, Is.Null);

// GOOD: Direct comparison respects Unity null semantics
Assert.That(component == null, Is.True, "Component should be null");
Assert.That(gameObject != null, Is.True, "GameObject should exist");
```

### MessageRegistrationToken Lifecycle

When testing with `MessageRegistrationToken`, always:

1. Set `MessageHandler.active = true` when creating the handler
1. Pass the local `MessageBus` to avoid using the global bus
1. Call `Enable()` after creating the token to activate registrations

```csharp
// GOOD: Complete setup with active handler, local bus, and Enable()
MessageBus messageBus = new MessageBus();
MessageHandler handler = new MessageHandler(instanceId, messageBus) { active = true };
MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);
token.Enable();

// BAD: Missing active = true - handler ignores all messages
MessageHandler handler = new MessageHandler(instanceId, messageBus);
// handler.active is false by default!

// BAD: Missing Enable() - handlers are staged but not activated
MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);
// Handlers are registered but inactive!

// BAD: Missing messageBus - uses global state, causes test pollution
MessageRegistrationToken token = MessageRegistrationToken.Create(handler);
```

## Anti-Patterns to Avoid

### Don't Use Underscores in Test Names

```csharp
// BAD
[Test]
public void Message_Bus_Should_Handle_Null_Input() { }

// GOOD
[Test]
public void MessageBusHandlesNullInput() { }
```

This rule is enforced by the pre-commit hook `fix-csharp-underscore-methods`, which auto-converts underscored method names to PascalCase before commit.

Local validation helpers:

- `node scripts/fix-csharp-underscore-methods.js --check <files...>`
- `node scripts/fix-csharp-underscore-methods.js --check --all`
- `node scripts/fix-csharp-underscore-methods.js <files...>`

Note: `git commit --no-verify` bypasses pre-commit hooks, so avoid using it for regular development flows.

### Don't Use Regions

```csharp
// BAD
#region Happy Path Tests
[Test]
public void Test1() { }
#endregion
```

### Avoid var - Use Explicit Types

```csharp
// BAD
var result = GetSomething();

// GOOD
MessageRegistrationToken token = messageBus.Register(handler);
```

### Don't Ignore Test Failures

```csharp
[Test]
[Ignore("Flaky")]
public void SometimesFailingTest() { }
```

### Don't Use Description Annotations

```csharp
[Test]
[Description("Tests that messages are delivered to handlers")]
public void TestMessageDelivery() { }
```

### Don't Create Tests Without Assertions

```csharp
[Test]
public void CreateMessageBus()
{
    MessageBus bus = new MessageBus();
    // No assertions
}
```

## Testing Checklist

- [ ] Happy path tests exist for all new public methods
- [ ] Negative tests cover null/invalid inputs
- [ ] Edge cases test boundaries
- [ ] Unexpected usage is handled gracefully
- [ ] No underscores in test names
- [ ] No regions in test code
- [ ] Explicit types used (avoid var)
- [ ] Clear failure messages on assertions
- [ ] IEnumerator used for UnityTest, not async Task
- [ ] Unity Object null checks use direct comparison

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Test Organization and Assertions](test-coverage-organization-assertions.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
