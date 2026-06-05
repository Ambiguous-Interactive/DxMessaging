---
title: "Data-Driven Tests with TestCaseSource"
id: "data-driven-tests"
category: "testing"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-01-21"

source:
  repository: "wallstop/unity-helpers"
  files:
    - path: "Tests/Runtime/SpriteSettingsTests.cs"
    - path: "Tests/Runtime/ParabolaTests.cs"
  url: "https://github.com/wallstop/unity-helpers"

tags:
  - "testing"
  - "parameterized"
  - "data-driven"
  - "nunit"
  - "test-cases"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of NUnit's parameterized test features"

impact:
  performance:
    rating: "low"
    details: "No runtime impact; test execution time unchanged"
  maintainability:
    rating: "high"
    details: "Reduces test duplication significantly"
  testability:
    rating: "high"
    details: "Easy to add new test cases"

prerequisites:
  - "Understanding of NUnit attributes"

dependencies:
  packages: []
  skills: []

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"

aliases:
  - "Parameterized tests"
  - "TestCaseSource"
  - "TestCase attribute"

related:
  - "test-categories"
  - "test-diagnostics"
  - "data-driven-tests-sources"
  - "data-driven-tests-usage"

status: "stable"
---

# Data-Driven Tests with TestCaseSource

> **One-line summary**: Use NUnit's `[TestCase]` and `[TestCaseSource]` attributes to run the same test logic with multiple inputs, reducing duplication and improving coverage.

## Overview

Data-driven testing separates test logic from test data:

1. **Test method** defines what to test
1. **Test cases** define the inputs and expected outputs

This pattern eliminates copy-paste tests and makes adding new cases trivial.

## Problem Statement

```csharp
// BAD: Duplicated test methods
[Test]
public void ParseIntegerZero()
{
    Assert.AreEqual(0, Parser.ParseInt("0"));
}

[Test]
public void ParseIntegerPositive()
{
    Assert.AreEqual(42, Parser.ParseInt("42"));
}

[Test]
public void ParseIntegerNegative()
{
    Assert.AreEqual(-5, Parser.ParseInt("-5"));
}

[Test]
public void ParseIntegerWithSpaces()
{
    Assert.AreEqual(7, Parser.ParseInt("  7  "));
}

// 4 methods doing the same thing with different data!
```

## Solution

### Using TestCase Attribute

```csharp
namespace WallstopStudios.UnityHelpers.Tests
{
    using NUnit.Framework;

    [TestFixture]
    public sealed class ParserTests
    {
        [Test]
        [TestCase("0", 0)]
        [TestCase("42", 42)]
        [TestCase("-5", -5)]
        [TestCase("  7  ", 7)]
        [TestCase("999999", 999999)]
        [TestCase("-999999", -999999)]
        public void ParseIntegerHandlesValidInput(string input, int expected)
        {
            int result = Parser.ParseInt(input);
            Assert.AreEqual(
                expected,
                result,
                $"Input '{input}' should parse to {expected}, got {result}");
        }

        [Test]
        [TestCase("")]
        [TestCase("abc")]
        [TestCase("12.34")]
        [TestCase(null)]
        public void ParseIntegerThrowsOnInvalidInput(string input)
        {
            Assert.Throws<FormatException>(
                () => Parser.ParseInt(input),
                $"Input '{input ?? "null"}' should throw FormatException");
        }
    }
}
```

## Performance Notes

- **Test Discovery**: Slight overhead for TestCaseSource reflection
- **Execution**: Same as regular tests
- **Memory**: Test cases are created once per test run

## Best Practices

### Do

- Use `[TestCase]` for simple, inline cases (2-5 cases)
- Use `[TestCaseSource]` for many cases or complex data
- Include descriptive failure messages with all parameters
- Use `SetName()` for human-readable test names
- Group related test cases in named methods

### Don't

- Don't duplicate test logic across test methods
- Don't forget to test edge cases (null, empty, bounds)
- Don't use TestCaseSource for single test cases (overhead)
- Don't make test case generation overly complex

### Organizing Test Cases

```csharp
// Group by category
private static IEnumerable<TestCaseData> HappyPathCases() { }
private static IEnumerable<TestCaseData> EdgeCases() { }
private static IEnumerable<TestCaseData> ErrorCases() { }

// Or by feature
private static IEnumerable<TestCaseData> ParsingCases() { }
private static IEnumerable<TestCaseData> FormattingCases() { }
private static IEnumerable<TestCaseData> ValidationCases() { }
```

## Related Patterns

- [Test Categories](./test-categories.md) - Organizing tests
- [Data-Driven Test Sources](./data-driven-tests-sources.md) - Advanced TestCaseSource patterns
- [Data-Driven Test Usage Patterns](./data-driven-tests-usage.md) - Naming and diagnostics
