## Overview

Continuation material extracted from `readonly-struct-cached-hash.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
+--------------------------------------------------------------+
|  FastVector2Int (readonly struct)                            |
+--------------------------------------------------------------+
|  readonly int x = 5                                          |
|  readonly int y = 10                                         |
|  readonly int _hash = 0x7A3B2C1D  <- Computed ONCE in ctor   |
+--------------------------------------------------------------+
|  GetHashCode() -> return _hash    <- O(1), no computation    |
|  Equals(other) -> if (_hash != other._hash) return false;    |
|                   return x == other.x && y == other.y;       |
|                   ^ Early-out on hash mismatch               |
+--------------------------------------------------------------+
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Core.DataStructure
{
    using System;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// High-performance 2D integer coordinate for use as dictionary key.
    /// Pre-computes hash at construction; implements IEquatable to avoid boxing.
    /// </summary>
    public readonly struct FastVector2Int : IEquatable<FastVector2Int>
    {
        public readonly int x;
        public readonly int y;
        private readonly int _hash;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastVector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
            _hash = ComputeHash(x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeHash(int x, int y)
        {
            // High-quality hash combining
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x;
                hash = hash * 31 + y;
                return hash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _hash;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(FastVector2Int other)
        {
            // Early-out: different hash = definitely not equal
            // Same hash = probably equal, verify with full comparison
            return _hash == other._hash && x == other.x && y == other.y;
        }

        public override bool Equals(object obj)
        {
            return obj is FastVector2Int other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FastVector2Int left, FastVector2Int right)
        {
            return left.Equals(right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FastVector2Int left, FastVector2Int right)
        {
            return !left.Equals(right);
        }

        public override string ToString() => $"({x}, {y})";

        // Implicit conversion from Unity's Vector2Int
        public static implicit operator FastVector2Int(UnityEngine.Vector2Int v)
        {
            return new FastVector2Int(v.x, v.y);
        }

        public static implicit operator UnityEngine.Vector2Int(FastVector2Int v)
        {
            return new UnityEngine.Vector2Int(v.x, v.y);
        }
    }
}
```

### 3D Variant

```csharp
public readonly struct FastVector3Int : IEquatable<FastVector3Int>
{
    public readonly int x;
    public readonly int y;
    public readonly int z;
    private readonly int _hash;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastVector3Int(int x, int y, int z)
    {
        this.x = x;
        this.y = y;
        this.z = z;

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + x;
            hash = hash * 31 + y;
            hash = hash * 31 + z;
            _hash = hash;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _hash;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FastVector3Int other)
    {
        return _hash == other._hash
            && x == other.x
            && y == other.y
            && z == other.z;
    }

    // ... operators, Equals(object), etc.
}
```

## See Also

- [Readonly Struct with Cached Hash for Dictionary Keys](./readonly-struct-cached-hash.md)
