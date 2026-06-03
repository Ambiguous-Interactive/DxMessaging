---
title: "Data-Driven Coverage Patterns"
id: "test-coverage-data-driven"
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
  - "data-driven"
  - "nunit"
  - "coverage"

complexity:
  level: "intermediate"
  reasoning: "Requires structuring reusable data sets for tests"

impact:
  performance:
    rating: "none"
    details: "Testing pattern only"
  maintainability:
    rating: "high"
    details: "Reduces duplication while increasing coverage"
  testability:
    rating: "high"
    details: "Encourages broad coverage with fewer tests"

prerequisites:
  - "Understanding of NUnit TestCase/TestCaseSource"

dependencies:
  packages: []
  skills:
    - "comprehensive-test-coverage"
    - "data-driven-tests"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
    - "NUnit"

aliases:
  - "Parameterized coverage"

related:
  - "comprehensive-test-coverage"
  - "data-driven-tests"

status: "stable"
---

# Data-Driven Coverage Patterns

> **One-line summary**: Use `[TestCase]` and `[TestCaseSource]` to cover many scenarios without duplicating test code.

## Overview

Data-driven tests are the fastest way to expand coverage while keeping test code maintainable.

## Problem Statement

Duplicated tests increase maintenance cost and still miss edge cases. Parameterized tests keep coverage broad and readable.

## Solution

### Using TestCase for Inline Data

```csharp
[TestFixture]
public sealed class MessageParsingTests
{
    [Test]
    [TestCase("valid", true)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("   ", false)]
    [TestCase("valid-with-dashes", true)]
    [TestCase("123numeric", true)]
    public void IsValidMessageIdReturnsExpectedResult(string messageId, bool expected)
    {
        bool result = MessageValidator.IsValidMessageId(messageId);

        Assert.That(
            result,
            Is.EqualTo(expected),
            $"MessageId '{messageId ?? "null"}' should be {(expected ? "valid" : "invalid")}");
    }
}
```

### Using TestCaseSource for Complex Data

```csharp
[TestFixture]
public sealed class TargetResolutionTests
{
    private static IEnumerable<TestCaseData> GameObjectTargetingTestCases
    {
        get
        {
            yield return new TestCaseData(new Vector3(0, 0, 0), "Origin", true)
                .SetName("OriginPositionIsValidTarget");
            yield return new TestCaseData(new Vector3(float.NaN, 0, 0), "Invalid", false)
                .SetName("NaNPositionIsInvalidTarget");
        }
    }

    [Test]
    [TestCaseSource(nameof(GameObjectTargetingTestCases))]
    public void ValidateTargetPositionReturnsExpectedResult(
        Vector3 position,
        string objectName,
        bool expectedValidity)
    {
        bool result = TargetValidator.IsValidTargetPosition(position);

        Assert.That(
            result,
            Is.EqualTo(expectedValidity),
            $"Position {position} for '{objectName}' should be {(expectedValidity ? "valid" : "invalid")}");
    }
}
```

### Consolidating Test Logic

```csharp
[TestFixture]
public sealed class MessageSerializationTests
{
    private static readonly Type[] SupportedMessageTypes =
    {
        typeof(SimpleMessage),
        typeof(ComplexMessage),
        typeof(NestedMessage),
        typeof(CollectionMessage)
    };

    [Test]
    [TestCaseSource(nameof(SupportedMessageTypes))]
    public void MessageTypeRoundTripsCorrectly(Type messageType)
    {
        object original = Activator.CreateInstance(messageType);
        string serialized = MessageSerializer.Serialize(original);
        object deserialized = MessageSerializer.Deserialize(serialized, messageType);

        Assert.That(deserialized, Is.EqualTo(original));
    }
}
```

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Data-Driven Tests](data-driven-tests.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
