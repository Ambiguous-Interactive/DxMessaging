---
title: "Array Pooling with ArrayPool and Custom Pools Part 2"
id: "array-pooling-part-2"
category: "performance"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-03-16"
status: "stable"
tags:
  - migration
  - split
complexity:
  level: "intermediate"
impact:
  performance:
    rating: "low"
---

## Overview

Continuation material extracted from `array-pooling.md` to keep .llm files within the 300-line budget.

## Solution

## Performance Notes

- **Allocations**: Zero in steady state after pool warm-up
- **WallstopArrayPool**: ~50ns overhead for clear on return
- **WallstopFastArrayPool**: ~10ns overhead (no clear)
- **SystemArrayPool**: May return larger arrays (power of 2 sizing)
- **LOH**: Arrays > 85KB go on Large Object Heap; pool to avoid LOH fragmentation

## Best Practices

### Do

- Use `WallstopFastArrayPool` for unmanaged types when you don't need zeroed data
- Use `WallstopArrayPool` for sensitive data (passwords, keys)
- Use `SystemArrayPool` when exact size doesn't matter
- Check `array.Length` vs requested size when using SystemArrayPool

### Don't

- Don't hold PooledArray beyond the using scope
- Don't mix pool types (get from one, return to another)
- Don't assume SystemArrayPool arrays are exactly the requested size
- Don't pool tiny arrays (overhead exceeds benefit for <64 bytes)

## Related Patterns

- [Collection Pooling](./collection-pooling.md) - List/HashSet/etc. pooling
- [Array Pooling Usage Examples](./array-pooling-usage-examples.md) - Applied pooling scenarios

## See Also

- [Array Pooling with ArrayPool and Custom Pools](./array-pooling.md)
