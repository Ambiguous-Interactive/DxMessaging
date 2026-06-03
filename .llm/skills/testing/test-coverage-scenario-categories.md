---
title: "Test Coverage Scenario Categories"
id: "test-coverage-scenario-categories"
category: "testing"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Core/NominalTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "coverage"
  - "edge-cases"
  - "negative-tests"

complexity:
  level: "intermediate"
  reasoning: "Requires designing tests across multiple scenario classes"

impact:
  performance:
    rating: "none"
    details: "Testing pattern only"
  maintainability:
    rating: "high"
    details: "Comprehensive scenarios prevent regressions"
  testability:
    rating: "critical"
    details: "Defines the breadth of coverage expected"

prerequisites:
  - "Understanding of NUnit testing framework"

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
  - "Scenario coverage"

related:
  - "comprehensive-test-coverage"
  - "test-coverage-data-driven"

status: "stable"
---

# Test Coverage Scenario Categories

> **One-line summary**: Cover normal, negative, edge, unexpected, and "impossible" scenarios for every change.

## Overview

Coverage is incomplete if it only exercises the happy path. Use these scenario categories for every feature or fix.

## Problem Statement

Tests that only cover expected usage miss boundary behavior, error handling, and defensive code paths.

## Solution

### 1. Normal / Happy Path Scenarios

```csharp
[TestFixture]
public sealed class MessageBusHappyPathTests
{
    [Test]
    public void RegisterHandlerSucceedsWithValidInput()
    {
        MessageBus messageBus = new MessageBus();
        bool handlerCalled = false;

        MessageRegistrationToken token = messageBus.RegisterUntargetedHandler<TestMessage>(
            _ => handlerCalled = true);

        Assert.That(token, Is.Not.Null, "Registration should return a valid token");
        Assert.That(token.IsActive, Is.True, "Token should be active after registration");
        Assert.That(handlerCalled, Is.False, "Handler should not be called yet");
    }
}
```

### 2. Negative Scenarios (Error Conditions)

```csharp
[TestFixture]
public sealed class MessageBusNegativeTests
{
    [Test]
    public void RegisterHandlerThrowsOnNullHandler()
    {
        MessageBus messageBus = new MessageBus();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => messageBus.RegisterUntargetedHandler<TestMessage>(null));

        Assert.That(
            exception.ParamName,
            Does.Contain("handler").IgnoreCase,
            "Exception should identify the null parameter");
    }
}
```

### 3. Edge Cases (Boundary Conditions)

```csharp
[TestFixture]
public sealed class MessageBusEdgeCaseTests
{
    [Test]
    public void EmitWithNoHandlersDoesNotThrow()
    {
        MessageBus messageBus = new MessageBus();
        TestMessage message = new TestMessage { Value = 1 };

        Assert.DoesNotThrow(
            () => message.EmitUntargeted(messageBus),
            "Emitting with no registered handlers should be safe");
    }

    [TestCase(0)]
    [TestCase(int.MaxValue)]
    [TestCase(int.MinValue)]
    public void MessageWithExtremeIntValuesHandledCorrectly(int extremeValue)
    {
        MessageBus messageBus = new MessageBus();
        int receivedValue = -999;

        messageBus.RegisterUntargetedHandler<TestMessage>(msg => receivedValue = msg.Value);

        TestMessage message = new TestMessage { Value = extremeValue };
        message.EmitUntargeted(messageBus);

        Assert.That(receivedValue, Is.EqualTo(extremeValue));
    }
}
```

### 4. Unexpected Situations

```csharp
[TestFixture]
public sealed class MessageBusUnexpectedUsageTests
{
    [Test]
    public void DuplicateRegistrationCreatesDistinctTokens()
    {
        MessageBus messageBus = new MessageBus();
        int invokeCount = 0;
        Action<TestMessage> handler = _ => invokeCount++;

        MessageRegistrationToken token1 = messageBus.RegisterUntargetedHandler(handler);
        MessageRegistrationToken token2 = messageBus.RegisterUntargetedHandler(handler);

        TestMessage message = new TestMessage();
        message.EmitUntargeted(messageBus);

        Assert.That(invokeCount, Is.EqualTo(2));
        Assert.That(token1, Is.Not.SameAs(token2));
    }
}
```

### 5. "The Impossible" Scenarios

```csharp
[TestFixture]
public sealed class MessageBusDefensiveTests
{
    [Test]
    public void HandlerThrowingExceptionDoesNotPreventOtherHandlers()
    {
        MessageBus messageBus = new MessageBus();
        bool handler1Called = false;
        bool handler3Called = false;

        messageBus.RegisterUntargetedHandler<TestMessage>(_ => handler1Called = true);
        messageBus.RegisterUntargetedHandler<TestMessage>(
            _ => throw new InvalidOperationException("Simulated failure"));
        messageBus.RegisterUntargetedHandler<TestMessage>(_ => handler3Called = true);

        TestMessage message = new TestMessage();

        try
        {
            message.EmitUntargeted(messageBus);
        }
        catch (InvalidOperationException)
        {
        }

        Assert.That(handler1Called, Is.True, "Handler before exception should be called");
        // Behavior of handler3 depends on implementation choice
    }
}
```

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Data-Driven Coverage Patterns](test-coverage-data-driven.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
