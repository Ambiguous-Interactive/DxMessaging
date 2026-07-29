---
name: cache-eviction
description: "Build a bounded cache with LRU, LFU, SLRU, FIFO, or Random eviction, write/access TTL, and hit-rate statistics via the struct-based CacheBuilder. Use when a Dictionary is being used as a cache, when memoizing an expensive query or computation, when memory grows without bound, or when picking an eviction policy for an access pattern."
metadata:
  category: "performance"
  tags: "caching, memory, performance, lru, lfu, eviction, data-structures"
---

# Bounded Cache with Eviction Policies

A cache stores results of expensive computation or IO and discards entries by a
declared policy. An unbounded `Dictionary` used as a cache is a memory leak; a
cache without expiration serves stale data.

## When to use

- Memoizing a database query, file load, or expensive computation.
- A `Dictionary<K, V>` field that only ever grows.
- Choosing between LRU, LFU, SLRU, FIFO, and Random for a known access pattern.
- Adding TTL semantics to cached data that goes stale.
- Instrumenting hit rate before tuning a cache size.

## Rules

- Build through `CacheBuilder<TKey, TValue>`, never by constructing `Cache<,>`
  directly. The builder is a `struct`, so configuration allocates nothing, and it
  supplies the defaults (`MaximumSize` falls back to 1000 when left at or below
  zero, `EvictionPolicy.Lru`).
- Always set `WithMaximumSize(n)`. Size it from entry size against a memory
  budget, not by guess.
- The builder methods are `WithMaximumSize`, `WithPolicy`, `WithExpireAfterWrite`,
  `WithExpireAfterAccess`, `WithRecordStats`, and `WithKeyComparer`; `Build()`
  materializes a `CacheOptions<TKey, TValue>` and returns the cache.
- Pick the policy from the access pattern: LRU for general purpose, LFU when
  frequency matters more than recency, SLRU to protect a hot set from a cold
  scan, FIFO for time-based freshness regardless of access, Random only where
  eviction cost is irrelevant - it is O(n) per eviction while LRU and FIFO are
  O(1).
- `Cache<TKey, TValue>` is `IDisposable` and holds a `syncLock` around every
  `TryGet`, `Put`, and `Dispose`. Use `using` at the declaration site, and treat
  a disposed cache as a permanent miss (`TryGet` returns `false`, `Put` is a
  no-op) rather than an exception.
- Expiration is evaluated lazily on access. `IsExpired` checks
  `ExpireAfterWriteSeconds` against `WriteTime` and `ExpireAfterAccessSeconds`
  against `LastAccessTime`; an expired entry is removed and counted as a MISS,
  not a hit. Nothing sweeps expired entries in the background.
- `Put` on an existing key updates the value, refreshes both timestamps, and
  promotes to most-recently-used without evicting. `Put` of a new key evicts in a
  `while (entries.Count >= MaximumSize)` loop first.
- Only LRU and SLRU reorder on access (`PromoteToMru`). FIFO and LFU deliberately
  leave the list order alone, so do not add promotion to them.
- Time comes from `UnityEngine.Time.realtimeSinceStartup`, so entry ages advance
  only while the Unity player runs.
- Turn on `WithRecordStats()` while tuning and read `GetStats()` for `HitCount`,
  `MissCount`, `EvictionCount`, `Size`, and `HitRate`. Choose the policy from
  measured hit rate, not from intuition.
- Supply `WithKeyComparer` for struct keys so lookups avoid boxing; the default
  is `EqualityComparer<TKey>.Default`. See `value-equality-and-hashing`.
- Do not cache data that changes faster than its TTL; the cache then serves
  stale values at full cost.

## Cost

`TryGet` and `Put` are O(1) average case. Eviction is O(1) for LRU, SLRU, and
FIFO, and O(n) for Random (it walks the access list to the chosen index). Each
entry carries a `LinkedListNode<TKey>` plus value, write time, last-access time,
and access count.

## References

| Document                                                                                        | Purpose                                                                                          |
| ----------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| [cache-eviction-policies.md](./references/cache-eviction-policies.md)                           | Policy selection table, the unbounded-dictionary failure, and sizing guidance                    |
| [cache-eviction-builder.md](./references/cache-eviction-builder.md)                             | `CacheBuilder<TKey, TValue>` struct implementation, every `With*` method, and stats usage        |
| [cache-eviction-implementation.md](./references/cache-eviction-implementation.md)               | Entry point describing the dictionary plus linked-list design                                    |
| [cache-eviction-implementation-part-1.md](./references/cache-eviction-implementation-part-1.md) | Full `Cache<TKey, TValue>`, `CacheOptions`, `CacheStats`, eviction, expiration, and locking code |
| [cache-eviction-implementation-part-2.md](./references/cache-eviction-implementation-part-2.md) | Cross-links and changelog for the implementation split                                           |
