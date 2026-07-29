# Readonly Struct with Cached Hash for Dictionary Keys

> **One-line summary**: Pre-compute hash codes at construction time for value types used as dictionary keys, eliminating repeated hash calculations and enabling hash-based early-out in equality checks.

## Overview

When using structs as dictionary keys, each lookup calls `GetHashCode()` and potentially `Equals()`. By:

1. Computing the hash once at construction
1. Storing it in a readonly field
1. Using it as an early-out in `Equals()`

We achieve optimal dictionary performance with zero allocations.

## Problem Statement

```csharp
// BAD: Unity's Vector2Int recomputes hash every call
public struct Vector2Int
{
    public int x, y;

    public override int GetHashCode()
    {
        // Computed every dictionary operation
        return x.GetHashCode() ^ (y.GetHashCode() << 2);
    }

    public override bool Equals(object obj)
    {
        // Boxing! Creates garbage for struct comparison
        if (obj is Vector2Int other)
            return x == other.x && y == other.y;
        return false;
    }
}
```

For a dictionary with 10,000 lookups/frame:

- 10,000 hash computations (unnecessary work)
- Potential boxing if `Equals(object)` is called
- No early-out optimization

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [readonly struct cached hash part 1](./readonly-struct-cached-hash-part-1.md)
- [readonly struct cached hash part 2](./readonly-struct-cached-hash-part-2.md)
