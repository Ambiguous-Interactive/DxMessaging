# Collection Extensions: Accessors

> **One-line summary**: Provide Try-pattern accessors and null-safe checks for common collection types.

## Overview

Accessor helpers make collections safer to use while avoiding allocations from LINQ. They should be documented with complexity and allocation notes.

## Problem Statement

Many access patterns either throw on empty collections or allocate enumerators. Try-pattern accessors avoid both issues.

## Solution

### Core Concept

Provide small, inlinable helpers for first/last/random access and null/empty checks.

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Core.Extensions
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;

    public static class IReadOnlyListExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetFirst<T>(this IReadOnlyList<T> list, out T value)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (list.Count > 0)
            {
                value = list[0];
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetLast<T>(this IReadOnlyList<T> list, out T value)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            int count = list.Count;
            if (count > 0)
            {
                value = list[count - 1];
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetRandom<T>(this IReadOnlyList<T> list)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (list.Count == 0)
            {
                throw new InvalidOperationException("Cannot get random element from empty list.");
            }

            return list[UnityEngine.Random.Range(0, list.Count)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetRandom<T>(this IReadOnlyList<T> list, out T value)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (list.Count > 0)
            {
                value = list[UnityEngine.Random.Range(0, list.Count)];
                return true;
            }

            value = default;
            return false;
        }
    }

    public static class CollectionExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty<T>(this IReadOnlyCollection<T> collection)
        {
            return collection == null || collection.Count == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty<T>(this T[] array)
        {
            return array == null || array.Length == 0;
        }

        public static bool IsNullOrEmpty<T>(this IEnumerable<T> enumerable)
        {
            if (enumerable == null)
            {
                return true;
            }

            if (enumerable is ICollection<T> collection)
            {
                return collection.Count == 0;
            }

            if (enumerable is IReadOnlyCollection<T> readOnlyCollection)
            {
                return readOnlyCollection.Count == 0;
            }

            using (IEnumerator<T> enumerator = enumerable.GetEnumerator())
            {
                return !enumerator.MoveNext();
            }
        }
    }
}
```

## Usage Examples

```csharp
if (enemies.TryGetFirst(out Enemy first))
{
    first.Attack();
}

if (items.IsNullOrEmpty())
{
    Debug.Log("No items");
}
```

## See Also

- [Collection Extension Methods](./collection-extensions.md)
- [Try-Pattern APIs](./try-pattern-apis.md)
- [Collection Extensions: Type Specialization](./collection-extensions-type-specialization.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
