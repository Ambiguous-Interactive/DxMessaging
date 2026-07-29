## Overview

Continuation material extracted from `array-pooling.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
+-------------------------------------------------------------+
|                    Array Pool Hierarchy                      |
+-------------------------------------------------------------+
|  WallstopArrayPool<T>     | Exact size, cleared, safe       |
|  WallstopFastArrayPool<T> | Exact size, not cleared, fast   |
|  SystemArrayPool<T>       | May be larger, wraps Shared     |
+-------------------------------------------------------------+

Usage:
+--------------------------------------------------------------+
| using PooledArray<T> lease = Pool.Get(size, out T[] arr);    |
| // arr.Length == size (Wallstop) or >= size (System)         |
| // Use arr...                                                |
| // Dispose returns to pool                                   |
+--------------------------------------------------------------+
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// RAII wrapper for pooled arrays.
    /// </summary>
    public readonly struct PooledArray<T> : IDisposable
    {
        private readonly T[] array;
        private readonly Action<T[]> returnAction;

        public T[] Array => array;
        public int Length => array?.Length ?? 0;

        internal PooledArray(T[] array, Action<T[]> returnAction)
        {
            this.array = array;
            this.returnAction = returnAction;
        }

        public void Dispose()
        {
            if (array != null)
            {
                returnAction?.Invoke(array);
            }
        }
    }

    /// <summary>
    /// Exact-size array pool that clears arrays on return.
    /// Safe for sensitive data.
    /// </summary>
    public static class WallstopArrayPool<T>
    {
        private static readonly Dictionary<int, Stack<T[]>> pools =
            new Dictionary<int, Stack<T[]>>();
        private static readonly object syncLock = new object();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PooledArray<T> Get(int exactSize, out T[] array)
        {
            lock (syncLock)
            {
                if (pools.TryGetValue(exactSize, out Stack<T[]> pool) && pool.Count > 0)
                {
                    array = pool.Pop();
                    return new PooledArray<T>(array, Return);
                }
            }

            array = new T[exactSize];
            return new PooledArray<T>(array, Return);
        }

        private static void Return(T[] array)
        {
            if (array == null) return;

            // Clear for security
            System.Array.Clear(array, 0, array.Length);

            lock (syncLock)
            {
                if (!pools.TryGetValue(array.Length, out Stack<T[]> pool))
                {
                    pool = new Stack<T[]>(4);
                    pools[array.Length] = pool;
                }

                if (pool.Count < 16) // Max per size
                {
                    pool.Push(array);
                }
            }
        }
    }

    /// <summary>
    /// Exact-size array pool that does NOT clear arrays.
    /// Fastest option for unmanaged types.
    /// </summary>
    public static class WallstopFastArrayPool<T> where T : unmanaged
    {
        private static readonly Dictionary<int, Stack<T[]>> pools =
            new Dictionary<int, Stack<T[]>>();
        private static readonly object syncLock = new object();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PooledArray<T> Get(int exactSize, out T[] array)
        {
            lock (syncLock)
            {
                if (pools.TryGetValue(exactSize, out Stack<T[]> pool) && pool.Count > 0)
                {
                    array = pool.Pop();
                    return new PooledArray<T>(array, Return);
                }
            }

            array = new T[exactSize];
            return new PooledArray<T>(array, Return);
        }

        private static void Return(T[] array)
        {
            if (array == null) return;

            // No clearing - faster but caller must not rely on zeroed data

            lock (syncLock)
            {
                if (!pools.TryGetValue(array.Length, out Stack<T[]> pool))
                {
                    pool = new Stack<T[]>(4);
                    pools[array.Length] = pool;
                }

                if (pool.Count < 16)
                {
                    pool.Push(array);
                }
            }
        }
    }

    /// <summary>
    /// Wraps ArrayPool<T>.Shared for variable-size needs.
    /// Returns arrays that may be larger than requested.
    /// </summary>
    public static class SystemArrayPool<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PooledArray<T> Get(int minimumSize, out T[] array)
        {
            array = ArrayPool<T>.Shared.Rent(minimumSize);
            return new PooledArray<T>(array, Return);
        }

        private static void Return(T[] array)
        {
            if (array != null)
            {
                ArrayPool<T>.Shared.Return(array, clearArray: false);
            }
        }
    }
}
```

## See Also

- [Array Pooling with ArrayPool and Custom Pools](./array-pooling.md)
