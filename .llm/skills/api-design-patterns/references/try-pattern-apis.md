# Try-Pattern APIs for Defensive Programming

> **One-line summary**: Implement `Try*` methods that return bool and use out parameters for operations that may fail, avoiding exceptions for expected failure cases.

## Overview

The Try-pattern provides a consistent way to handle operations that may legitimately fail:

1. **Returns bool** indicating success/failure
1. **Uses out parameter** for the result
1. **Never throws** for expected failures
1. **Zero allocation** on failure

This is the standard .NET pattern (see `int.TryParse`, `Dictionary.TryGetValue`).

## Problem Statement

```csharp
// BAD: Exceptions for expected cases
public T Get(int index)
{
    if (index < 0 || index >= count)
        throw new IndexOutOfRangeException(); // Expensive!
    return items[index];
}

// Usage requires try-catch
try
{
    var item = collection.Get(userIndex);
}
catch (IndexOutOfRangeException)
{
    // Handle missing item
}

// BAD: Null return is ambiguous
public T GetOrNull(int index)
{
    if (index < 0 || index >= count)
        return default; // What if T is int? default is 0, not "missing"
    return items[index];
}
```

## Solution

### Core Pattern

```csharp
namespace WallstopStudios.UnityHelpers.Core
{
    using System;
    using System.Runtime.CompilerServices;

    public sealed class SafeCollection<T>
    {
        private readonly T[] items;
        private readonly int count;

        /// <summary>
        /// Attempts to get an item at the specified index.
        /// </summary>
        /// <param name="index">The index to retrieve.</param>
        /// <param name="value">The item if found, default otherwise.</param>
        /// <returns>True if the index is valid and the item was retrieved.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(int index, out T value)
        {
            if ((uint)index < (uint)count) // Single comparison for both bounds
            {
                value = items[index];
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Gets an item at the index, or returns a default value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetOrDefault(int index, T defaultValue = default)
        {
            return TryGet(index, out T value) ? value : defaultValue;
        }

        /// <summary>
        /// Gets an item at the index, throwing if invalid.
        /// Use this only when an invalid index is a programming error.
        /// </summary>
        public T Get(int index)
        {
            if (!TryGet(index, out T value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    $"Index must be between 0 and {count - 1}");
            }
            return value;
        }
    }
}
```

## Performance Notes

### Exception Cost vs. Try-Pattern

| Scenario    | Try-Pattern | Exception |
| ----------- | ----------- | --------- |
| Success     | ~1ns        | ~1ns      |
| Failure     | ~1ns        | ~10,000ns |
| Stack trace | N/A         | +50,000ns |

### Memory

- **Try-Pattern**: Zero allocations on failure
- **Exception**: Allocates exception object, captures stack trace

## Best Practices

### Do

- Use Try-pattern when failure is expected/common
- Set out parameter to default on failure
- Provide GetOrDefault convenience method
- Keep throwing version for programming errors
- Use `(uint)index < (uint)count` for single-comparison bounds check

### Don't

- Don't use exceptions for expected failures
- Don't return null when T could legitimately be null
- Don't forget to initialize out parameter on all paths
- Don't use Try-pattern for truly exceptional conditions (disk full, etc.)

### When to Use Each Pattern

| Scenario                     | Pattern                     |
| ---------------------------- | --------------------------- |
| User input parsing           | TryParse                    |
| Cache lookup                 | TryGet                      |
| Optional config              | TryGet / GetOrDefault       |
| Programming error (null arg) | Throw ArgumentNullException |
| System failure (disk full)   | Throw IOException           |

## Related Patterns

- [Collection Extensions](./collection-extensions.md) - Extension method design
- [Try-Pattern API Variants](./try-pattern-apis-variants.md) - Dictionary, parse, and component styles
- [Try-Pattern API Usage Examples](./try-pattern-apis-usage.md) - Usage and chaining
