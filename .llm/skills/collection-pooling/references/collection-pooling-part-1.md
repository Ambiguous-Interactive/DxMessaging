## Overview

Continuation material extracted from `collection-pooling.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
+-------------------------------------------------------------+
|  using (PooledResource<List<T>> lease = Pool.Get(out list)) |
|  {                                                          |
|      // Use list...                                         |
|  } // <-- Dispose() called: list.Clear(), return to pool    |
+-------------------------------------------------------------+
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// RAII wrapper that returns a pooled object on Dispose.
    /// </summary>
    public readonly struct PooledResource<T> : IDisposable where T : class
    {
        private readonly T value;
        private readonly Action<T> returnAction;

        public T Value => value;

        internal PooledResource(T value, Action<T> returnAction)
        {
            this.value = value;
            this.returnAction = returnAction;
        }

        public void Dispose()
        {
            returnAction?.Invoke(value);
        }
    }

    /// <summary>
    /// Generic collection pool with typed accessors.
    /// </summary>
    public static class Buffers<T>
    {
        private static readonly ObjectPool<List<T>> listPool =
            new ObjectPool<List<T>>(
                initialCapacity: 8,
                resetAction: list => list.Clear()
            );

        private static readonly ObjectPool<HashSet<T>> hashSetPool =
            new ObjectPool<HashSet<T>>(
                initialCapacity: 4,
                resetAction: set => set.Clear()
            );

        private static readonly ObjectPool<Stack<T>> stackPool =
            new ObjectPool<Stack<T>>(
                initialCapacity: 4,
                resetAction: stack => stack.Clear()
            );

        private static readonly ObjectPool<Queue<T>> queuePool =
            new ObjectPool<Queue<T>>(
                initialCapacity: 4,
                resetAction: queue => queue.Clear()
            );

        public static class List
        {
            public static PooledResource<List<T>> Get(out List<T> list)
            {
                list = listPool.Rent();
                return new PooledResource<List<T>>(list, l =>
                {
                    l.Clear();
                    listPool.Return(l);
                });
            }
        }

        public static class HashSet
        {
            public static PooledResource<HashSet<T>> Get(out HashSet<T> set)
            {
                set = hashSetPool.Rent();
                return new PooledResource<HashSet<T>>(set, s =>
                {
                    s.Clear();
                    hashSetPool.Return(s);
                });
            }
        }

        public static class Stack
        {
            public static PooledResource<Stack<T>> Get(out Stack<T> stack)
            {
                stack = stackPool.Rent();
                return new PooledResource<Stack<T>>(stack, s =>
                {
                    s.Clear();
                    stackPool.Return(s);
                });
            }
        }

        public static class Queue
        {
            public static PooledResource<Queue<T>> Get(out Queue<T> queue)
            {
                queue = queuePool.Rent();
                return new PooledResource<Queue<T>>(queue, q =>
                {
                    q.Clear();
                    queuePool.Return(q);
                });
            }
        }
    }
}
```

## Usage

### Basic List Pooling

```csharp
public void ProcessEnemies(Vector3 playerPos)
{
    using PooledResource<List<Enemy>> lease = Buffers<Enemy>.List.Get(out List<Enemy> nearby);

    foreach (Enemy e in allEnemies)
    {
        if (Vector3.Distance(e.Position, playerPos) < 10f)
        {
            nearby.Add(e);
        }
    }

    foreach (Enemy e in nearby)
    {
        e.ReactToPlayer();
    }
    // lease.Dispose() called automatically: nearby.Clear() + return to pool
}
```

### Nested Collection Usage

```csharp
public void BuildGraph()
{
    using var nodesLease = Buffers<Node>.List.Get(out List<Node> nodes);
    using var visitedLease = Buffers<Node>.HashSet.Get(out HashSet<Node> visited);
    using var pendingLease = Buffers<Node>.Queue.Get(out Queue<Node> pending);

    pending.Enqueue(rootNode);

    while (pending.Count > 0)
    {
        Node current = pending.Dequeue();
        if (visited.Add(current))
        {
            nodes.Add(current);
            foreach (Node child in current.Children)
            {
                pending.Enqueue(child);
            }
        }
    }

    ProcessNodes(nodes);
} // All three collections cleared and returned
```

## See Also

- [Collection Pooling with RAII Pattern](./collection-pooling.md)
