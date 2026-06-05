---
title: "Readonly Struct with Cached Hash for Dictionary Keys Part 2"
id: "readonly-struct-cached-hash-part-2"
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

Continuation material extracted from `readonly-struct-cached-hash.md` to keep .llm files within the 300-line budget.

## Solution

## Usage

### As Dictionary Key

```csharp
// Tile lookup by position
private readonly Dictionary<FastVector2Int, Tile> tiles =
    new Dictionary<FastVector2Int, Tile>(1024);

public Tile GetTile(int x, int y)
{
    FastVector2Int key = new FastVector2Int(x, y);
    return tiles.TryGetValue(key, out Tile tile) ? tile : null;
}

public void SetTile(int x, int y, Tile tile)
{
    tiles[new FastVector2Int(x, y)] = tile;
}
```

### With HashSet

```csharp
private readonly HashSet<FastVector2Int> visitedCells =
    new HashSet<FastVector2Int>(256);

public bool TryVisit(int x, int y)
{
    return visitedCells.Add(new FastVector2Int(x, y));
}
```

### Conversion from Unity Types

```csharp
// Implicit conversion makes usage transparent
FastVector2Int fast = someVector2Int; // No explicit cast needed
UnityEngine.Vector2Int unity = fast;  // And back
```

## Best Practices

### Do

- Always implement `IEquatable<T>` for struct dictionary keys
- Use `readonly struct` to prevent defensive copies
- Apply `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to hot methods
- Provide implicit conversions for interop with framework types
- Use `unchecked` for hash computation (overflow is fine)

### Don't

- Don't rely on default struct equality (uses reflection)
- Don't forget to implement both `==` and `!=` operators
- Don't use mutable fields in hash computation
- Don't use this pattern for rarely-hashed types (overhead not worth it)

### Hash Function Quality

For good distribution:

```csharp
// Good: Prime multiplication
hash = hash * 31 + field;

// Also good: HashCode.Combine (C# 8+)
_hash = HashCode.Combine(x, y);

// Also good: Unity-style XOR with shift
_hash = x ^ (y << 2);
```

## Related Patterns

- [IEquatable Usage Examples](../solid/iequatable-implementation-usage.md) - Full equality pattern
- [Aggressive Inlining](./aggressive-inlining.md) - When to use MethodImpl
- [Readonly Struct Cached Hash Performance Notes](./readonly-struct-cached-hash-performance-notes.md) - Benchmarks and rationale

## See Also

- [Readonly Struct with Cached Hash for Dictionary Keys](./readonly-struct-cached-hash.md)
