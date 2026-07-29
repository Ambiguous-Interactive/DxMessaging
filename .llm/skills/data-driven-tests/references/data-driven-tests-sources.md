# Data-Driven Test Sources

> **One-line summary**: Advanced TestCaseSource patterns and reusable data sets.

## Overview

This skill explains advanced TestCaseSource patterns.

## Solution

Use these sources to build reusable data sets for tests.

### Using TestCaseSource for Complex Data

```csharp
[TestFixture]
public sealed class MatchModeTests
{
    // Static method providing test cases
    private static IEnumerable<TestCaseData> CaseSensitivityCases()
    {
        // Exact match
        yield return new TestCaseData(MatchMode.Exact, "Player", "Player", true)
            .SetName("Exact_SameCase_Matches");
        yield return new TestCaseData(MatchMode.Exact, "Player", "player", false)
            .SetName("Exact_DifferentCase_NoMatch");
        yield return new TestCaseData(MatchMode.Exact, "Player", "PLAYER", false)
            .SetName("Exact_AllCaps_NoMatch");

        // Case insensitive
        yield return new TestCaseData(MatchMode.CaseInsensitive, "Player", "player", true)
            .SetName("CaseInsensitive_LowerCase_Matches");
        yield return new TestCaseData(MatchMode.CaseInsensitive, "Player", "PLAYER", true)
            .SetName("CaseInsensitive_UpperCase_Matches");

        // Contains
        yield return new TestCaseData(MatchMode.Contains, "Player", "Play", true)
            .SetName("Contains_Prefix_Matches");
        yield return new TestCaseData(MatchMode.Contains, "Player", "layer", true)
            .SetName("Contains_Suffix_Matches");
        yield return new TestCaseData(MatchMode.Contains, "Player", "xyz", false)
            .SetName("Contains_Missing_NoMatch");

        // Edge cases
        yield return new TestCaseData(MatchMode.Exact, "", "", true)
            .SetName("Exact_EmptyStrings_Match");
        yield return new TestCaseData(MatchMode.Exact, "Test", null, false)
            .SetName("Exact_NullPattern_NoMatch");
    }

    [Test]
    [TestCaseSource(nameof(CaseSensitivityCases))]
    public void MatchModeMatchesCorrectly(
        MatchMode matchMode,
        string text,
        string pattern,
        bool expectedMatch)
    {
        bool actualMatch = Matcher.IsMatch(text, pattern, matchMode);

        Assert.AreEqual(
            expectedMatch,
            actualMatch,
            $"MatchMode={matchMode}, Text='{text}', Pattern='{pattern ?? "null"}'. " +
            $"Expected match={expectedMatch}, actual={actualMatch}");
    }
}
```

### Using Static Property for Test Cases

```csharp
[TestFixture]
public sealed class VectorMathTests
{
    // Property providing test data
    private static IEnumerable<TestCaseData> DotProductCases
    {
        get
        {
            yield return new TestCaseData(
                new Vector3(1, 0, 0),
                new Vector3(1, 0, 0),
                1f)
                .SetName("Parallel_SameDirection");

            yield return new TestCaseData(
                new Vector3(1, 0, 0),
                new Vector3(-1, 0, 0),
                -1f)
                .SetName("Parallel_OppositeDirection");

            yield return new TestCaseData(
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
                0f)
                .SetName("Perpendicular");

            yield return new TestCaseData(
                new Vector3(3, 4, 0),
                new Vector3(4, 3, 0),
                24f)
                .SetName("Arbitrary_3D");
        }
    }

    [Test]
    [TestCaseSource(nameof(DotProductCases))]
    public void DotProductCalculatesCorrectly(Vector3 a, Vector3 b, float expected)
    {
        float result = Vector3.Dot(a, b);
        Assert.AreEqual(expected, result, 0.0001f);
    }
}
```

### Using External Test Data Class

```csharp
// Separate class for test data (useful for sharing across fixtures)
public static class ColorConversionTestData
{
    public static IEnumerable<TestCaseData> RgbToHexCases
    {
        get
        {
            yield return new TestCaseData(Color.red, "#FF0000");
            yield return new TestCaseData(Color.green, "#00FF00");
            yield return new TestCaseData(Color.blue, "#0000FF");
            yield return new TestCaseData(Color.white, "#FFFFFF");
            yield return new TestCaseData(Color.black, "#000000");
            yield return new TestCaseData(new Color(0.5f, 0.5f, 0.5f), "#808080");
        }
    }

    public static IEnumerable<TestCaseData> HexToRgbCases
    {
        get
        {
            yield return new TestCaseData("#FF0000", Color.red);
            yield return new TestCaseData("#ff0000", Color.red); // lowercase
            yield return new TestCaseData("FF0000", Color.red);  // no #
        }
    }
}

[TestFixture]
public sealed class ColorConversionTests
{
    [Test]
    [TestCaseSource(typeof(ColorConversionTestData), nameof(ColorConversionTestData.RgbToHexCases))]
    public void ToHexConvertsCorrectly(Color color, string expectedHex)
    {
        string result = ColorUtils.ToHex(color);
        Assert.AreEqual(expectedHex, result, $"Color {color} should convert to {expectedHex}");
    }

    [Test]
    [TestCaseSource(typeof(ColorConversionTestData), nameof(ColorConversionTestData.HexToRgbCases))]
    public void FromHexConvertsCorrectly(string hex, Color expectedColor)
    {
        Color result = ColorUtils.FromHex(hex);
        Assert.AreEqual(expectedColor.r, result.r, 0.01f);
        Assert.AreEqual(expectedColor.g, result.g, 0.01f);
        Assert.AreEqual(expectedColor.b, result.b, 0.01f);
    }
}
```

### Combining Multiple Test Case Sources

```csharp
[TestFixture]
public sealed class BoundaryTests
{
    private static int[] ValidValues => new[] { 0, 1, 50, 99, 100 };
    private static int[] InvalidLowValues => new[] { -100, -1 };
    private static int[] InvalidHighValues => new[] { 101, 200, 1000 };

    [Test]
    [TestCaseSource(nameof(ValidValues))]
    public void AcceptsValidValues(int value)
    {
        Assert.DoesNotThrow(() => validator.Validate(value));
    }

    [Test]
    [TestCaseSource(nameof(InvalidLowValues))]
    [TestCaseSource(nameof(InvalidHighValues))]
    public void RejectsInvalidValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => validator.Validate(value));
    }
}
```
