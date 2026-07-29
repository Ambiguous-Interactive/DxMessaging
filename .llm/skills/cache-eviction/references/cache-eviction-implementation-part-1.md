## Overview

Continuation material extracted from `cache-eviction-implementation.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

- Dictionary for O(1) lookup
- Linked list for eviction order
- Stats for hit/miss/eviction tracking
- Optional TTL expiration on access or write

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Core.Cache
{
    using System;
    using System.Collections.Generic;
    public enum EvictionPolicy
    {
        Lru,
        Lfu,
        Slru,
        Fifo,
        Random
    }
    public sealed class CacheOptions<TKey, TValue>
    {
        public int MaximumSize { get; set; } = 1000;
        public EvictionPolicy Policy { get; set; } = EvictionPolicy.Lru;
        public float? ExpireAfterWriteSeconds { get; set; }
        public float? ExpireAfterAccessSeconds { get; set; }
        public bool RecordStats { get; set; }
        public IEqualityComparer<TKey> KeyComparer { get; set; }
    }
    public sealed class Cache<TKey, TValue> : IDisposable
    {
        private readonly Dictionary<TKey, CacheEntry> entries;
        private readonly LinkedList<TKey> accessOrder;
        private readonly CacheOptions<TKey, TValue> options;
        private readonly object syncLock = new object();
        private long hitCount;
        private long missCount;
        private long evictionCount;
        private bool disposed;
        private sealed class CacheEntry
        {
            public TValue Value;
            public LinkedListNode<TKey> Node;
            public float WriteTime;
            public float LastAccessTime;
            public int AccessCount;
        }
        internal Cache(CacheOptions<TKey, TValue> options)
        {
            this.options = options;
            IEqualityComparer<TKey> comparer = options.KeyComparer ?? EqualityComparer<TKey>.Default;
            entries = new Dictionary<TKey, CacheEntry>(options.MaximumSize, comparer);
            accessOrder = new LinkedList<TKey>();
        }
        public bool TryGet(TKey key, out TValue value)
        {
            if (disposed)
            {
                value = default;
                return false;
            }
            lock (syncLock)
            {
                if (entries.TryGetValue(key, out CacheEntry entry))
                {
                    float now = GetCurrentTime();
                    if (IsExpired(entry, now))
                    {
                        RemoveEntry(key, entry);
                        value = default;
                        missCount++;
                        return false;
                    }
                    entry.LastAccessTime = now;
                    entry.AccessCount++;
                    PromoteToMru(entry);
                    hitCount++;
                    value = entry.Value;
                    return true;
                }
                missCount++;
                value = default;
                return false;
            }
        }
        public void Put(TKey key, TValue value)
        {
            if (disposed)
            {
                return;
            }
            lock (syncLock)
            {
                float now = GetCurrentTime();
                if (entries.TryGetValue(key, out CacheEntry existing))
                {
                    existing.Value = value;
                    existing.WriteTime = now;
                    existing.LastAccessTime = now;
                    PromoteToMru(existing);
                    return;
                }
                while (entries.Count >= options.MaximumSize)
                {
                    EvictOne();
                }
                LinkedListNode<TKey> node = accessOrder.AddFirst(key);
                entries[key] = new CacheEntry
                {
                    Value = value,
                    Node = node,
                    WriteTime = now,
                    LastAccessTime = now,
                    AccessCount = 1
                };
            }
        }
        public CacheStats GetStats()
        {
            return new CacheStats
            {
                HitCount = hitCount,
                MissCount = missCount,
                EvictionCount = evictionCount,
                Size = entries.Count,
                HitRate = hitCount + missCount > 0
                    ? (double)hitCount / (hitCount + missCount)
                    : 0
            };
        }
        private void EvictOne()
        {
            if (accessOrder.Count == 0)
            {
                return;
            }
            TKey keyToEvict = options.Policy switch
            {
                EvictionPolicy.Lru => accessOrder.Last.Value,
                EvictionPolicy.Fifo => accessOrder.Last.Value,
                EvictionPolicy.Random => GetRandomKey(),
                _ => accessOrder.Last.Value
            };
            if (entries.TryGetValue(keyToEvict, out CacheEntry entry))
            {
                RemoveEntry(keyToEvict, entry);
                evictionCount++;
            }
        }
        private void PromoteToMru(CacheEntry entry)
        {
            if (options.Policy == EvictionPolicy.Lru || options.Policy == EvictionPolicy.Slru)
            {
                accessOrder.Remove(entry.Node);
                accessOrder.AddFirst(entry.Node);
            }
        }
        private bool IsExpired(CacheEntry entry, float now)
        {
            if (options.ExpireAfterWriteSeconds.HasValue)
            {
                if (now - entry.WriteTime > options.ExpireAfterWriteSeconds.Value)
                {
                    return true;
                }
            }
            if (options.ExpireAfterAccessSeconds.HasValue)
            {
                if (now - entry.LastAccessTime > options.ExpireAfterAccessSeconds.Value)
                {
                    return true;
                }
            }
            return false;
        }
        private void RemoveEntry(TKey key, CacheEntry entry)
        {
            accessOrder.Remove(entry.Node);
            entries.Remove(key);
        }
        private TKey GetRandomKey()
        {
            int index = UnityEngine.Random.Range(0, entries.Count);
            LinkedListNode<TKey> node = accessOrder.First;
            for (int i = 0; i < index; i++)
            {
                node = node.Next;
            }
            return node.Value;
        }
        private static float GetCurrentTime()
        {
            return UnityEngine.Time.realtimeSinceStartup;
        }
        public void Dispose()
        {
            disposed = true;
            lock (syncLock)
            {
                entries.Clear();
                accessOrder.Clear();
            }
        }
    }
    public struct CacheStats
    {
        public long HitCount;
        public long MissCount;
        public long EvictionCount;
        public int Size;
        public double HitRate;
    }
}
```

## See Also

- [Cache Eviction Implementation](./cache-eviction-implementation.md)
