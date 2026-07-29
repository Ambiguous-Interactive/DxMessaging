# Test Diagnostics and Investigation Patterns

> **One-line summary**: Implement diagnostic collectors and toggleable logging to understand complex test failures without polluting normal test output.

## Overview

When tests fail in complex systems (spatial queries, state machines, algorithms), stack traces aren't enough. This pattern provides:

1. **Diagnostic Collectors**: Record detailed execution traces
1. **Toggleable Diagnostics**: Zero overhead when disabled
1. **Filtered Logging**: Focus on specific components
1. **Investigation Markers**: Document intentional edge cases

## Problem Statement

```csharp
// BAD: Test fails with unhelpful message
[Test]
public void SpatialQueryReturnsNearbyObjects()
{
    tree.Insert(point1);
    tree.Insert(point2);
    // ...
    var results = tree.Query(bounds);

    Assert.AreEqual(5, results.Count); // Fails: Expected 5, got 3
    // WHY? Which points were missed? What did the query visit?
}
```

## Solution

### Diagnostic Collector Pattern

```csharp
namespace WallstopStudios.UnityHelpers.Tests.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using UnityEngine;

    /// <summary>
    /// Records detailed trace of spatial query execution for debugging.
    /// </summary>
    public sealed class SpatialQueryDiagnosticsCollector : IOctTreeQueryLogger
    {
        public struct NodeVisit
        {
            public Bounds NodeBounds;
            public int PointCount;
            public bool FullyContained;
            public bool Intersects;
            public VisitResult Result;
        }

        public enum VisitResult { Skipped, Partial, Full }

        private readonly List<NodeVisit> _nodeVisits = new List<NodeVisit>(64);
        private readonly List<Vector3> _pointsEvaluated = new List<Vector3>(128);
        private readonly List<Vector3> _pointsIncluded = new List<Vector3>(64);
        private readonly List<Vector3> _pointsExcluded = new List<Vector3>(64);
        private Bounds _queryBounds;

        public void BeginQuery(Bounds queryBounds)
        {
            _queryBounds = queryBounds;
            _nodeVisits.Clear();
            _pointsEvaluated.Clear();
            _pointsIncluded.Clear();
            _pointsExcluded.Clear();
        }

        public void LogNodeVisit(Bounds nodeBounds, int pointCount, bool fullyContained, bool intersects, VisitResult result)
        {
            _nodeVisits.Add(new NodeVisit
            {
                NodeBounds = nodeBounds,
                PointCount = pointCount,
                FullyContained = fullyContained,
                Intersects = intersects,
                Result = result
            });
        }

        public void LogPointEvaluated(Vector3 point, bool included)
        {
            _pointsEvaluated.Add(point);
            if (included)
                _pointsIncluded.Add(point);
            else
                _pointsExcluded.Add(point);
        }

        /// <summary>
        /// Build a detailed report for test failure investigation.
        /// </summary>
        public string BuildReport(
            ICollection<Vector3> expected,
            ICollection<Vector3> actual,
            int maxItems = 32)
        {
            StringBuilder sb = new StringBuilder(2048);

            sb.AppendLine("=== SPATIAL QUERY DIAGNOSTICS ===");
            sb.AppendLine();
            sb.AppendLine($"Query Bounds: {_queryBounds}");
            sb.AppendLine($"Expected Count: {expected.Count}");
            sb.AppendLine($"Actual Count: {actual.Count}");
            sb.AppendLine();

            // Missing points
            var missing = new HashSet<Vector3>(expected);
            foreach (var p in actual) missing.Remove(p);

            if (missing.Count > 0)
            {
                sb.AppendLine($"MISSING POINTS ({missing.Count}):");
                int shown = 0;
                foreach (var p in missing)
                {
                    if (shown++ >= maxItems) { sb.AppendLine("  ..."); break; }
                    bool inBounds = _queryBounds.Contains(p);
                    sb.AppendLine($"  {p} (in bounds: {inBounds})");
                }
                sb.AppendLine();
            }

            // Extra points
            var extra = new HashSet<Vector3>(actual);
            foreach (var p in expected) extra.Remove(p);

            if (extra.Count > 0)
            {
                sb.AppendLine($"EXTRA POINTS ({extra.Count}):");
                int shown = 0;
                foreach (var p in extra)
                {
                    if (shown++ >= maxItems) { sb.AppendLine("  ..."); break; }
                    sb.AppendLine($"  {p}");
                }
                sb.AppendLine();
            }

            // Node visit summary
            sb.AppendLine($"NODE VISITS ({_nodeVisits.Count}):");
            int skipped = 0, partial = 0, full = 0;
            foreach (var visit in _nodeVisits)
            {
                switch (visit.Result)
                {
                    case VisitResult.Skipped: skipped++; break;
                    case VisitResult.Partial: partial++; break;
                    case VisitResult.Full: full++; break;
                }
            }
            sb.AppendLine($"  Skipped: {skipped}, Partial: {partial}, Full: {full}");
            sb.AppendLine();

            // Sample node visits
            sb.AppendLine("SAMPLE NODE VISITS:");
            for (int i = 0; i < Math.Min(6, _nodeVisits.Count); i++)
            {
                var v = _nodeVisits[i];
                sb.AppendLine($"  [{v.Result}] points={v.PointCount} contained={v.FullyContained} intersects={v.Intersects}");
            }

            return sb.ToString();
        }
    }
}
```

## Related Patterns

- [Test Base Class Cleanup](../../test-fixtures-and-cleanup/references/test-base-class-cleanup.md) - Test infrastructure
- [Test Categories](../../unity-test-execution/references/test-categories.md) - Organizing diagnostic tests
- [Test Diagnostics Patterns](./test-diagnostics-patterns.md) - Toggleable and edge-case diagnostics
- [Test Diagnostics Usage](./test-diagnostics-usage.md) - Applying diagnostics in tests
