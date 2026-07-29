# Array Pooling with ArrayPool and Custom Pools

> **One-line summary**: Use specialized array pools for temporary buffers to eliminate allocations in serialization, networking, and data processing.

## Overview

Arrays are frequently allocated for temporary operations like:

- Serialization buffers
- Network packet buffers
- Image/texture data
- Computational scratch space

Three pool types serve different needs:

| Pool Type                  | Exact Size | Clears Data | Best For                      |
| -------------------------- | ---------- | ----------- | ----------------------------- |
| `WallstopArrayPool<T>`     | Yes        | Yes         | Security-sensitive, exact-fit |
| `WallstopFastArrayPool<T>` | Yes        | No          | Unmanaged types, max speed    |
| `SystemArrayPool<T>`       | No         | No          | Variable sizes, standard .NET |

## Problem Statement

```csharp
// BAD: Allocates 4KB every call
public void ProcessPacket(NetworkPacket packet)
{
    byte[] buffer = new byte[4096]; // Large Object Heap if > 85KB!
    int bytesRead = packet.ReadInto(buffer);
    ProcessBytes(buffer, 0, bytesRead);
    // buffer becomes garbage
}
```

For networking at 60 packets/second = 14.4 MB/minute of garbage.

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [array pooling part 1](./array-pooling-part-1.md)
- [array pooling part 2](./array-pooling-part-2.md)
