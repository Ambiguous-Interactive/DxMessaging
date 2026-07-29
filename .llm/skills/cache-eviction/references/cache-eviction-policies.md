# High-Performance Cache with Eviction Policies

> **One-line summary**: Build caches with LRU/LFU/SLRU eviction, TTL expiration, and statistics using a fluent builder.

## Overview

A well-designed cache reduces expensive computation or IO by storing results and evicting entries using a predictable policy. This pattern provides:

1. **Multiple eviction policies**: LRU, LFU, SLRU, FIFO, Random
1. **Time-based expiration**: Expire after write or access
1. **Size limits**: Maximum entry count with automatic eviction
1. **Statistics**: Hit/miss rates and eviction counts

## Problem Statement

```csharp
// BAD: No caching - expensive operation every call
public GameData LoadGameData(string playerId)
{
    return database.Query<GameData>(playerId); // 50ms per call
}

// BAD: Unbounded cache - memory leak
private Dictionary<string, GameData> cache = new Dictionary<string, GameData>();

public GameData LoadGameDataCached(string playerId)
{
    if (!cache.TryGetValue(playerId, out GameData data))
    {
        data = database.Query<GameData>(playerId);
        cache[playerId] = data; // Grows forever!
    }
    return data;
}
```

## Solution

### Core Concept

```text
Cache<K, V>
- MaxSize: 1000 entries
- Policy: LRU (Least Recently Used)
- ExpireAfterWrite: 5 minutes
- ExpireAfterAccess: 1 minute
```

### Basic Usage

```csharp
using Cache<string, GameData> cache = new CacheBuilder<string, GameData>()
    .WithMaximumSize(1000)
    .WithPolicy(EvictionPolicy.Lru)
    .Build();

if (!cache.TryGet(playerId, out GameData data))
{
    data = database.Query<GameData>(playerId);
    cache.Put(playerId, data);
}
```

## Eviction Policy Guide

| Policy | Best For             | Characteristics                    |
| ------ | -------------------- | ---------------------------------- |
| LRU    | General purpose      | Evicts least recently accessed     |
| LFU    | Frequency matters    | Evicts least frequently accessed   |
| SLRU   | Hot/cold data        | Protects frequently accessed items |
| FIFO   | Time-based freshness | Evicts oldest regardless of access |
| Random | Simple, uniform      | tracking overhead                  |

## Best Practices

### Do

- Size cache based on entry size and memory budget
- Enable stats during tuning to pick a policy
- Choose eviction policy based on access patterns
- Dispose caches when no longer needed

### Don't

- Don't use unbounded dictionaries as caches
- Don't cache data that changes too frequently
- Don't use Random eviction for performance-critical code

## See Also

- [Cache Eviction Implementation](./cache-eviction-implementation.md)
- [Cache Builder Configuration](./cache-eviction-builder.md)
- [Object Pooling](../../object-pooling/references/object-pooling.md)
- [Fluent Builder Pattern](../../api-design-patterns/references/fluent-builder-pattern.md)

## References

- [Cache design overview](https://en.wikipedia.org/wiki/Cache_replacement_policies)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
