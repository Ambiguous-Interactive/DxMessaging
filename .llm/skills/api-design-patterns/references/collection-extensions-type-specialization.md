# Collection Extensions: Type Specialization

> **One-line summary**: Use concrete-type fast paths to avoid interface dispatch overhead for hot collection operations.

## Overview

Interface calls are slower than direct calls. When collections are commonly `T[]` or `List<T>`, add specialized branches to avoid interface dispatch.

## Problem Statement

Generic `IReadOnlyList<T>` access is flexible but introduces virtual dispatch overhead on every access.

## Solution

### Core Concept

Provide fast paths for common concrete types, with a generic fallback.

### Implementation

```csharp
public static class CollectionExtensions
{
    public static int BinarySearch<T>(this IReadOnlyList<T> list, T value)
        where T : IComparable<T>
    {
        if (list is T[] array)
        {
            return Array.BinarySearch(array, value);
        }

        if (list is List<T> concreteList)
        {
            return concreteList.BinarySearch(value);
        }

        return BinarySearchGeneric(list, value);
    }

    private static int BinarySearchGeneric<T>(IReadOnlyList<T> list, T value)
        where T : IComparable<T>
    {
        int low = 0;
        int high = list.Count - 1;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int comparison = list[mid].CompareTo(value);

            if (comparison == 0)
            {
                return mid;
            }
            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }
}
```

## Performance Notes

```csharp
// Interface call: ~5ns overhead per call
IReadOnlyList<int> list = myList;
int first = list[0];

// Direct call: ~0.5ns
List<int> concreteList = myList;
int firstConcrete = concreteList[0];
```

## Usage Example

```csharp
List<int> sortedList = GetSortedScores();
int index = sortedList.BinarySearch(targetScore);

if (index >= 0)
{
    Debug.Log($"Found at index {index}");
}
else
{
    int insertionPoint = ~index;
    Debug.Log($"Would insert at index {insertionPoint}");
}
```

## See Also

- [Collection Extension Methods](./collection-extensions.md)
- [Collection Extensions: Accessors](./collection-extensions-accessors.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
