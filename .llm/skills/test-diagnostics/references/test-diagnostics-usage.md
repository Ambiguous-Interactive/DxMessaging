# Test Diagnostics Usage

> **One-line summary**: How to apply diagnostics in tests, with performance guidance.

## Overview

This skill explains how to use diagnostics in tests and CI.

## Solution

Follow the guidance below to enable diagnostics without noise.

## Usage

### Using Diagnostics in Tests

```csharp
[TestFixture]
public sealed class SpatialQueryTests : CommonTestBase
{
    [Test]
    public void QueryReturnsAllPointsInBounds()
    {
        // Enable diagnostics for this test
        var diagnostics = new SpatialQueryDiagnosticsCollector();
        tree.SetQueryLogger(diagnostics);

        // Setup
        var points = new List<Vector3>();
        for (int i = 0; i < 100; i++)
        {
            var point = Random.insideUnitSphere * 10;
            points.Add(point);
            tree.Insert(point);
        }

        // Query
        Bounds queryBounds = new Bounds(Vector3.zero, Vector3.one * 5);
        diagnostics.BeginQuery(queryBounds);
        var results = tree.Query(queryBounds);

        // Calculate expected
        var expected = points.Where(p => queryBounds.Contains(p)).ToList();

        // Assert with diagnostics on failure
        if (results.Count != expected.Count)
        {
            string report = diagnostics.BuildReport(expected, results);
            Assert.Fail($"Query returned wrong count.\n\n{report}");
        }

        Assert.AreEqual(expected.Count, results.Count);
    }
}
```

### Enabling Diagnostics for Debugging

```csharp
[TestFixture]
public sealed class StateMachineTests : CommonTestBase
{
    [SetUp]
    public override void CommonSetUp()
    {
        base.CommonSetUp();

        // Enable diagnostics for this specific component
        EditorDiagnostics.Enabled = true;
        EditorDiagnostics.NameFilter = "PlayerStateMachine";
    }

    [TearDown]
    public override void TearDown()
    {
        EditorDiagnostics.Enabled = false;
        EditorDiagnostics.NameFilter = null;
        base.TearDown();
    }

    [Test]
    public void TransitionsCorrectly()
    {
        // Now all PlayerStateMachine logs will appear
        stateMachine.TransitionTo(State.Running);
    }
}
```

## Performance Notes

- **Disabled Diagnostics**: Zero overhead (early return before string formatting)
- **Enabled Diagnostics**: ~1us per log call
- **Collectors**: Memory proportional to events collected

## Best Practices

### Do

- Disable diagnostics by default
- Use collectors for complex algorithms (spatial, pathfinding)
- Include all relevant context in diagnostic reports
- Use consistent prefixes for log filtering
- Document UNH-SUPPRESS markers with reasons

### Don't

- Don't leave diagnostics enabled in production
- Don't collect unbounded data (limit with maxItems)
- Don't use diagnostics for simple pass/fail tests
- Don't suppress warnings without documenting why

### Diagnostic Collector Template

```csharp
public sealed class MyAlgorithmDiagnostics
{
    private readonly List<StepRecord> _steps = new List<StepRecord>();

    public void RecordStep(string description, object state)
    {
        _steps.Add(new StepRecord(description, state.ToString()));
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ALGORITHM TRACE ===");
        foreach (var step in _steps)
        {
            sb.AppendLine($"[{step.Index}] {step.Description}: {step.State}");
        }
        return sb.ToString();
    }
}
```
