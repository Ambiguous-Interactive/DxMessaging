# StringBuilder Pooling for Zero-Allocation String Building

> **One-line summary**: Pool StringBuilder instances to eliminate per-operation allocations when building strings in hot paths.

## Overview

String concatenation with `+` or `+=` creates new string objects for each operation, causing O(n^2) allocations for n concatenations. StringBuilder avoids this, but creating a new StringBuilder each time still allocates. Pooling StringBuilders eliminates even that allocation.

## Problem Statement

```csharp
// BAD: O(n^2) allocations
public string BuildItemList(List<Item> items)
{
    string result = "";
    for (int i = 0; i < items.Count; i++)
    {
        result += items[i].Name; // New string EACH iteration
        if (i < items.Count - 1)
        {
            result += ", "; // Another allocation
        }
    }
    return result;
}
```

For 100 items, this creates ~199 temporary strings that immediately become garbage.

```csharp
// BETTER but still allocates StringBuilder
public string BuildItemList(List<Item> items)
{
    StringBuilder sb = new StringBuilder(256); // Allocates ~256 bytes
    // ... build string ...
    return sb.ToString();
}
```

## Solution

### Core Concept

Pool StringBuilders and clear them on return:

```text
+----------------------------------------+
|  StringBuilder Pool                    |
|  +----------+ +----------+ +--------+  |
|  | Cap:256  | | Cap:512  | |Cap:1024|  |
|  +----------+ +----------+ +--------+  |
+----------------+-----------------------+
                 | Get(capacity_hint)
                 v
           +-----------+
           | Use & sb  | sb.Clear() + Return
           | .Append() |--------------------+
           +-----------+                    |
                 | sb.ToString()            |
                 v                          v
            "result"                    Pool
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using System;
    using System.Text;

    public static class Buffers
    {
        private static readonly ObjectPool<StringBuilder> stringBuilderPool =
            new ObjectPool<StringBuilder>(
                initialCapacity: 4,
                maxSize: 32,
                resetAction: sb => sb.Clear()
            );

        public static class StringBuilder
        {
            /// <summary>
            /// Get a pooled StringBuilder with optional capacity hint.
            /// </summary>
            public static PooledResource<System.Text.StringBuilder> Get(
                out System.Text.StringBuilder sb,
                int capacityHint = 256)
            {
                sb = stringBuilderPool.Rent();

                // Ensure minimum capacity without shrinking
                if (sb.Capacity < capacityHint)
                {
                    sb.Capacity = capacityHint;
                }

                return new PooledResource<System.Text.StringBuilder>(sb, ReturnToPool);
            }

            private static void ReturnToPool(System.Text.StringBuilder sb)
            {
                // Clear content but preserve capacity
                sb.Clear();

                // Optional: Trim oversized builders to prevent memory bloat
                if (sb.Capacity > 8192)
                {
                    sb.Capacity = 256;
                }

                stringBuilderPool.Return(sb);
            }
        }

        /// <summary>
        /// Convenience method matching common API.
        /// </summary>
        public static PooledResource<System.Text.StringBuilder> GetStringBuilder(
            int capacityHint,
            out System.Text.StringBuilder sb)
        {
            return StringBuilder.Get(out sb, capacityHint);
        }
    }
}
```

## See Also

- [stringbuilder pooling part 1](./stringbuilder-pooling-part-1.md)
