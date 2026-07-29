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
