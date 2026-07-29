# Collection Extension Methods with Performance Documentation

> **One-line summary**: Create collection extension methods with explicit performance documentation and type-specialized overloads.

## Overview

Extension methods provide a fluent, discoverable API for collection operations. This pattern focuses on:

1. **Performance documentation** in XML comments
1. **Type-specialized implementations** for common collection types
1. **Try-pattern APIs** for safe access
1. **Zero-allocation** implementations where possible

## Problem Statement

```csharp
// BAD: LINQ allocates iterators
var first = list.FirstOrDefault(); // Allocates IEnumerator

// BAD: No documentation about performance
public static T GetRandom<T>(this IList<T> list)
{
    return list[Random.Range(0, list.Count)];
}
```

## Solution

### Core Pattern: Documented Extension Methods

Use XML remarks to capture complexity, allocations, and thread safety:

```csharp
/// <summary>
/// Brief description.
/// </summary>
/// <remarks>
/// <para>Performance: O(?) where ? is...</para>
/// <para>Allocations: No allocations / Allocates X bytes.</para>
/// <para>Thread Safety: Thread-safe / Not thread-safe.</para>
/// </remarks>
```

## Best Practices

### Do

- Document performance in XML comments
- Provide type-specialized overloads for hot paths
- Use Try-pattern for operations that can fail
- Throw on null with clear parameter names
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for tiny methods

### Don't

- Don't hide allocations (document them)
- Don't use LINQ in hot paths (allocates iterators)
- Don't forget null checks
- Don't assume interface performance equals concrete type performance

## See Also

- [Collection Extensions: Accessors](./collection-extensions-accessors.md)
- [Collection Extensions: Type Specialization](./collection-extensions-type-specialization.md)
- [Collection Extensions: Shuffle](./collection-extensions-shuffle.md)
- [Try-Pattern APIs](./try-pattern-apis.md)
- [Fluent Builder Pattern](./fluent-builder-pattern.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
