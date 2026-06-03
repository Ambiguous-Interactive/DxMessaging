---
title: "Data-Driven Test Usage Patterns"
id: "data-driven-tests-usage"
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
  - "parameterized"
  - "data-driven"
  - "nunit"

complexity:
  level: "intermediate"
  reasoning: "Builds on NUnit parameterized tests with reusable sources"

impact:
  performance:
    rating: "none"
    details: "Testing patterns only"
  maintainability:
    rating: "high"
    details: "Reduces duplicated test code"
  testability:
    rating: "high"
    details: "Encourages broad coverage"

prerequisites:
  - "Understanding of NUnit"

dependencies:
  packages: []
  skills:
    - "data-driven-tests"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
    - "NUnit"

aliases:
  - "TestCaseSource patterns"

related:
  - "data-driven-tests"

status: "stable"
---

# Data-Driven Test Usage Patterns

> **One-line summary**: Usage tips for naming, failures, and diagnostics.

## Overview

This skill highlights naming and diagnostics for data-driven tests.

## Solution

Use the patterns below to keep parameterized tests readable.

## Usage

### Descriptive Failure Messages

```csharp
[Test]
[TestCase("abc", "ABC")]
[TestCase("Hello World", "HELLO WORLD")]
public void ToUpperConvertsCorrectly(string input, string expected)
{
    string result = input.ToUpper();

    // Include all relevant info in failure message
    Assert.AreEqual(
        expected,
        result,
        $"Input: '{input}'\nExpected: '{expected}'\nActual: '{result}'");
}
```

### Using SetName for Clear Test Names

```csharp
private static IEnumerable<TestCaseData> EdgeCases()
{
    yield return new TestCaseData("", 0)
        .SetName("EmptyString_ReturnsZero");

    yield return new TestCaseData((string)null, 0)
        .SetName("NullString_ReturnsZero");

    yield return new TestCaseData("   ", 0)
        .SetName("WhitespaceOnly_ReturnsZero");
}
```
