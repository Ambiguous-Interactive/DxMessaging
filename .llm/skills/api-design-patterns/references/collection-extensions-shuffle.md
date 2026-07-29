# Collection Extensions: Shuffle

> **One-line summary**: Shuffle lists in-place using Fisher-Yates with optional deterministic Random.

## Overview

Shuffling is a common utility. The Fisher-Yates algorithm provides an unbiased, in-place shuffle in O(n) time.

## Problem Statement

Naive shuffle implementations introduce bias or allocate intermediate lists.

## Solution

### Implementation

```csharp
public static class ListExtensions
{
    public static void Shuffle<T>(this IList<T> list)
    {
        if (list == null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = UnityEngine.Random.Range(0, n + 1);
            T temp = list[k];
            list[k] = list[n];
            list[n] = temp;
        }
    }

    public static void Shuffle<T>(this IList<T> list, System.Random random)
    {
        if (list == null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        if (random == null)
        {
            throw new ArgumentNullException(nameof(random));
        }

        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            T temp = list[k];
            list[k] = list[n];
            list[n] = temp;
        }
    }
}
```

## Usage Example

```csharp
List<int> cards = BuildDeck();
cards.Shuffle();
```

## Testing Considerations

- Inject `System.Random` to make shuffle deterministic in tests
- Assert on permutations by comparing sorted sequences

## See Also

- [Collection Extension Methods](./collection-extensions.md)
- [Collection Extensions: Accessors](./collection-extensions-accessors.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
