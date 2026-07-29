## Overview

Continuation material extracted from `object-pooling.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

Instead of `new`, acquire objects from a pool. Instead of letting them become garbage, return them to the pool:

```text
+---------------------------------------------------------+
|                     Object Pool                         |
|  +-----+ +-----+ +-----+ +-----+ +-----+                |
|  | Msg | | Msg | | Msg | | Msg | | Msg | ... (idle)     |
|  +-----+ +-----+ +-----+ +-----+ +-----+                |
+--------------------+------------------------------------+
                     |
         +-----------+-----------+
         | Rent()                | Return()
         v                       |
    +---------+                  |
    | Active  |------------------+
    | Message |
    +---------+
```

### Implementation

```csharp
namespace DxMessaging.Core.Pool
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Thread-safe object pool with automatic growth.
    /// </summary>
    /// <typeparam name="T">Type of pooled objects.</typeparam>
    public sealed class ObjectPool<T> where T : class, new()
    {
        private readonly Stack<T> pool;
        private readonly Action<T> resetAction;
        private readonly object syncLock = new object();
        private readonly int maxSize;

        public int CountInactive
        {
            get
            {
                lock (syncLock)
                {
                    return pool.Count;
                }
            }
        }

        public ObjectPool(int initialCapacity = 16, int maxSize = 1024, Action<T> resetAction = null)
        {
            this.maxSize = maxSize;
            this.resetAction = resetAction;
            this.pool = new Stack<T>(initialCapacity);

            // Pre-warm the pool
            for (int i = 0; i < initialCapacity; i++)
            {
                pool.Push(new T());
            }
        }

        /// <summary>
        /// Rent an object from the pool. Creates new if pool is empty.
        /// </summary>
        public T Rent()
        {
            lock (syncLock)
            {
                if (pool.Count > 0)
                {
                    return pool.Pop();
                }
            }
            // Pool exhausted, create new (will be returned to pool later)
            return new T();
        }

        /// <summary>
        /// Return an object to the pool for reuse.
        /// </summary>
        public void Return(T item)
        {
            if (item == null)
            {
                return;
            }

            // Reset state before returning to pool
            resetAction?.Invoke(item);

            lock (syncLock)
            {
                // Don't exceed max size - let excess become garbage
                if (pool.Count < maxSize)
                {
                    pool.Push(item);
                }
            }
        }

        /// <summary>
        /// Clear the pool, releasing all objects.
        /// </summary>
        public void Clear()
        {
            lock (syncLock)
            {
                pool.Clear();
            }
        }
    }
}
```

### Step-by-Step Breakdown

1. **Pre-warm on construction**: Allocate objects upfront during loading, not during gameplay
1. **Rent instead of new**: `Rent()` returns a pooled instance or creates one if pool is empty
1. **Reset on return**: Clear object state to prevent data leakage between uses
1. **Cap pool size**: Prevent unbounded memory growth during usage spikes
1. **Thread safety**: Lock-based synchronization for multi-threaded access

## Testing Considerations

- Test pool growth under load (pool expands correctly)
- Test max size cap (excess objects become garbage, not pooled)
- Test reset behavior (no data leakage between rentals)
- Test thread safety (concurrent rent/return doesn't corrupt state)

```csharp
[Test]
public void Pool_RentAndReturn_ReusesObjects()
{
    ObjectPool<TestMessage> pool = new ObjectPool<TestMessage>(initialCapacity: 1);

    TestMessage first = pool.Rent();
    pool.Return(first);

    TestMessage second = pool.Rent();

    Assert.That(ReferenceEquals(first, second), Is.True, "Pool should reuse returned objects");
}

[Test]
public void Pool_ExceedingMaxSize_DoesNotGrow()
{
    ObjectPool<TestMessage> pool = new ObjectPool<TestMessage>(initialCapacity: 2, maxSize: 2);

    TestMessage a = pool.Rent();
    TestMessage b = pool.Rent();
    TestMessage c = pool.Rent(); // Created, not from pool

    pool.Return(a);
    pool.Return(b);
    pool.Return(c); // Should be dropped, pool at max

    Assert.That(pool.CountInactive, Is.EqualTo(2), "Pool should not exceed max size");
}
```

## See Also

- [Object Pooling for Zero-Allocation Messaging](./object-pooling.md)
