## Overview

Continuation material extracted from `fluent-builder-pattern.md` to keep .llm files within the 300-line budget.

## Solution

### Struct-Based Fluent Builder

```csharp
namespace WallstopStudios.UnityHelpers.Core.Cache
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Zero-allocation fluent builder for Cache configuration.
    /// </summary>
    /// <remarks>
    /// This is a struct to avoid heap allocation. Each With* method returns
    /// a new struct by value, enabling method chaining.
    /// </remarks>
    public struct CacheBuilder<TKey, TValue>
    {
        private int maximumSize;
        private EvictionPolicy policy;
        private TimeSpan? expireAfterWrite;
        private TimeSpan? expireAfterAccess;
        private bool recordStats;
        private IEqualityComparer<TKey> keyComparer;
        private float? jitterFactor;
        private bool initialized;

        /// <summary>
        /// Sets the maximum number of entries in the cache.
        /// </summary>
        /// <param name="size">Maximum entry count. Must be positive.</param>
        public CacheBuilder<TKey, TValue> WithMaximumSize(int size)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
            }

            var copy = this;
            copy.maximumSize = size;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Sets the eviction policy when cache is full.
        /// </summary>
        /// <param name="evictionPolicy">Policy to use (LRU, LFU, etc.).</param>
        public CacheBuilder<TKey, TValue> WithPolicy(EvictionPolicy evictionPolicy)
        {
            var copy = this;
            copy.policy = evictionPolicy;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Sets expiration time after an entry is written.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithExpireAfterWrite(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive");
            }

            var copy = this;
            copy.expireAfterWrite = duration;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Sets expiration time after an entry is accessed.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithExpireAfterAccess(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive");
            }

            var copy = this;
            copy.expireAfterAccess = duration;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Enables hit/miss statistics recording.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithRecordStats()
        {
            var copy = this;
            copy.recordStats = true;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Sets a custom key comparer for lookups.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithKeyComparer(IEqualityComparer<TKey> comparer)
        {
            var copy = this;
            copy.keyComparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Adds random jitter to expiration times to prevent thundering herd.
        /// </summary>
        /// <param name="factor">Jitter factor (0.0-1.0). E.g., 0.1 = +/-10% variation.</param>
        public CacheBuilder<TKey, TValue> WithExpirationJitter(float factor)
        {
            if (factor < 0f || factor > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(factor), "Factor must be between 0 and 1");
            }

            var copy = this;
            copy.jitterFactor = factor;
            copy.initialized = true;
            return copy;
        }

        /// <summary>
        /// Builds the cache with the configured options.
        /// </summary>
        /// <returns>A new Cache instance.</returns>
        /// <exception cref="InvalidOperationException">If configuration is invalid.</exception>
        public Cache<TKey, TValue> Build()
        {
            // Apply defaults
            int size = maximumSize > 0 ? maximumSize : 1000;
            EvictionPolicy pol = initialized ? policy : EvictionPolicy.Lru;

            // Validate combinations
            if (jitterFactor.HasValue && !expireAfterWrite.HasValue && !expireAfterAccess.HasValue)
            {
                throw new InvalidOperationException(
                    "Jitter requires expiration to be set. Call WithExpireAfterWrite or WithExpireAfterAccess first.");
            }

            var options = new CacheOptions<TKey, TValue>
            {
                MaximumSize = size,
                Policy = pol,
                ExpireAfterWriteSeconds = (float?)expireAfterWrite?.TotalSeconds,
                ExpireAfterAccessSeconds = (float?)expireAfterAccess?.TotalSeconds,
                RecordStats = recordStats,
                KeyComparer = keyComparer,
                JitterFactor = jitterFactor
            };

            return new Cache<TKey, TValue>(options);
        }
    }
}
```

## Performance Notes

### Struct vs Class Builder

| Aspect        | Struct Builder | Class Builder     |
| ------------- | -------------- | ----------------- |
| Allocation    | 0 bytes        | ~24 bytes minimum |
| GC pressure   | None           | Creates garbage   |
| Copy behavior | Value copy     | Reference copy    |

### Method Chaining Cost

Each `With*` method copies the struct (~50-100 bytes typically). This is stack-allocated and very fast (~1ns).

## See Also

- [Fluent Builder Pattern with Struct Builders](./fluent-builder-pattern.md)
