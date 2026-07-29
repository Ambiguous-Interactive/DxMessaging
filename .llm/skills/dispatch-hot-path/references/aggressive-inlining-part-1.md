## Overview

Continuation material extracted from `aggressive-inlining.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
Without Inlining:                    With Inlining:
----------------- ----------------
call GetValue()    -+                sum += _value;
  push this         |  ~3-5 cycles   (directly inline)
  push return       |
  pop result       -+                ~1 cycle
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Core
{
    using System.Runtime.CompilerServices;

    public readonly struct BitSet
    {
        private readonly ulong[] bits;
        private readonly int count;

        /// <summary>
        /// Gets the number of bits that can be stored.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Capacity => bits.Length * 64;

        /// <summary>
        /// Gets the number of bits currently set to true.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Checks if a bit is set.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(int index)
        {
            int wordIndex = index >> 6;  // index / 64
            int bitIndex = index & 63;   // index % 64
            return (bits[wordIndex] & (1UL << bitIndex)) != 0;
        }

        /// <summary>
        /// Checks if this set contains all bits from another set.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsAll(BitSet other)
        {
            for (int i = 0; i < bits.Length; i++)
            {
                if ((bits[i] & other.bits[i]) != other.bits[i])
                    return false;
            }
            return true;
        }
    }
}
```

### Pattern Matching with Inline

```csharp
public readonly struct FastOneOf<T0, T1, T2>
{
    private readonly byte index;
    private readonly T0 value0;
    private readonly T1 value1;
    private readonly T2 value2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Is<T>(out T value)
    {
        if (typeof(T) == typeof(T0) && index == 0)
        {
            value = (T)(object)value0;
            return true;
        }
        if (typeof(T) == typeof(T1) && index == 1)
        {
            value = (T)(object)value1;
            return true;
        }
        if (typeof(T) == typeof(T2) && index == 2)
        {
            value = (T)(object)value2;
            return true;
        }
        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Match<TResult>(
        Func<T0, TResult> f0,
        Func<T1, TResult> f1,
        Func<T2, TResult> f2)
    {
        return index switch
        {
            0 => f0(value0),
            1 => f1(value1),
            2 => f2(value2),
            _ => throw new InvalidOperationException()
        };
    }
}
```

### Equality Operations

```csharp
public readonly struct FastVector2Int : IEquatable<FastVector2Int>
{
    public readonly int x;
    public readonly int y;
    private readonly int _hash;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _hash;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FastVector2Int other)
    {
        return _hash == other._hash && x == other.x && y == other.y;
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
}
```

## See Also

- [AggressiveInlining for Hot Path Optimization](./aggressive-inlining.md)
