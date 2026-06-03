---
title: "Test Organization and Assertions"
id: "test-coverage-organization-assertions"
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
  - "assertions"
  - "naming"
  - "organization"

complexity:
  level: "basic"
  reasoning: "Establishes consistent structure and assertion clarity"

impact:
  performance:
    rating: "none"
    details: "Testing guidance only"
  maintainability:
    rating: "high"
    details: "Consistent structure improves readability and review"
  testability:
    rating: "high"
    details: "Clear assertions improve diagnostics"

prerequisites:
  - "Understanding of NUnit"

dependencies:
  packages: []
  skills:
    - "comprehensive-test-coverage"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
    - "NUnit"

aliases:
  - "Test naming"
  - "Assertion best practices"

related:
  - "comprehensive-test-coverage"

status: "stable"
---

# Test Organization and Assertions

> **One-line summary**: Use clear naming, structured test classes, and expressive assertions with failure messages.

## Overview

Well-structured tests are easier to review and debug. Names should read like sentences and assertions should explain failures.

## Problem Statement

Poor naming and weak assertions make failures hard to interpret and increase maintenance cost.

## Solution

### Test Class Naming

```csharp
// Pattern: {ClassUnderTest}Tests or {Feature}Tests
public sealed class MessageBusTests { }
public sealed class TargetedMessageDeliveryTests { }
public sealed class RegistrationLifecycleTests { }
```

### Test Method Naming

```csharp
// GOOD: Descriptive, reads naturally
[Test]
public void EmitUntargetedMessageInvokesAllRegisteredHandlers() { }

// BAD: Underscores, unclear
[Test]
public void Test_Emit_Works() { }
```

Test method names must use PascalCase without underscores. The pre-commit hook `fix-csharp-underscore-methods` auto-fixes underscored method names when possible.

### Test Class Organization

```csharp
[TestFixture]
public sealed class MessageBusTests
{
    private MessageBus _messageBus;

    [SetUp]
    public void SetUp()
    {
        _messageBus = new MessageBus();
    }

    [TearDown]
    public void TearDown()
    {
        _messageBus?.Dispose();
    }

    [Test]
    public void RegisterHandlerSucceeds() { }

    [Test]
    public void RegisterNullHandlerThrows() { }
}
```

### Expressive Assertions

```csharp
Assert.That(
    actualCount,
    Is.EqualTo(expectedCount),
    $"Handler should be invoked {expectedCount} times, but was {actualCount}");

Assert.That(result, Is.Not.Null.And.Not.Empty);
Assert.That(values, Has.Count.EqualTo(5).And.All.GreaterThan(0));
```

### Assert One Concept Per Test

```csharp
[Test]
public void HandlerReceivesCorrectMessagePayload()
{
    TestMessage received = default;
    _messageBus.RegisterUntargetedHandler<TestMessage>(msg => received = msg);

    TestMessage sent = new TestMessage { Value = 42 };
    sent.EmitUntargeted(_messageBus);

    Assert.That(received.Value, Is.EqualTo(42));
}
```

### Use CollectionAssert for Collections

```csharp
CollectionAssert.AreEquivalent(
    new[] { typeof(MessageA), typeof(MessageB), typeof(MessageC) },
    types,
    "Should return all registered message types");
```

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Unity Considerations and Anti-Patterns](test-coverage-unity-anti-patterns.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
