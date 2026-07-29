# Collection Pooling with RAII Pattern

> **One-line summary**: Use PooledResource<T> with `using` statements to automatically rent and return collections, achieving zero-allocation collection usage in hot paths.

## Overview

Collection pooling provides reusable instances of common collection types (List, HashSet, Stack, Queue, StringBuilder) with automatic cleanup. The RAII (Resource Acquisition Is Initialization) pattern via `IDisposable` ensures collections are:

1. **Rented** when entering scope
1. **Cleared** when exiting scope (removing stale data)
1. **Returned** to the pool automatically

This eliminates the most common source of per-frame allocations in Unity games.

## Problem Statement

```csharp
// BAD: Allocates a new List every call
public void ProcessEnemies(Vector3 playerPos)
{
    List<Enemy> nearby = new List<Enemy>(); // 40+ byte allocation
    foreach (Enemy e in allEnemies)
    {
        if (Vector3.Distance(e.Position, playerPos) < 10f)
        {
            nearby.Add(e);
        }
    }
    // nearby becomes garbage after this method
}
```

Called 60 times per second = 2,400 List allocations per minute = GC spikes.

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [collection pooling part 1](./collection-pooling-part-1.md)
- [collection pooling part 2](./collection-pooling-part-2.md)
