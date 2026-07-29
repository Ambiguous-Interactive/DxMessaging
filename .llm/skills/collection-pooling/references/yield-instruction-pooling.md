# WaitForSeconds and Yield Instruction Pooling

> **One-line summary**: Cache and reuse Unity yield instructions like `WaitForSeconds` to eliminate per-coroutine allocations.

## Overview

Unity coroutines frequently use `yield return new WaitForSeconds(x)`, creating a new object each time. Since `WaitForSeconds` is reusable after completion, we can cache instances by duration to achieve zero-allocation coroutines.

## Problem Statement

```csharp
// BAD: Allocates 20 bytes every call
private IEnumerator SpawnEnemies()
{
    while (true)
    {
        SpawnEnemy();
        yield return new WaitForSeconds(2f); // New allocation!
    }
}
```

With 100 coroutines at 1 yield/second = 2KB/second = 7.2MB/hour of garbage.

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [yield instruction pooling part 1](./yield-instruction-pooling-part-1.md)
- [yield instruction pooling part 2](./yield-instruction-pooling-part-2.md)
