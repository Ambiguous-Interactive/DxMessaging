# Cache Eviction Implementation

> **One-line summary**: Implement a cache with eviction, expiration, and statistics using O(1) data structures.

## Overview

This implementation uses a dictionary for key lookup and a linked list for eviction ordering. Expiration checks are evaluated on access.

## Problem Statement

Caches without eviction or expiration lead to unbounded memory growth or stale data. This implementation provides clear eviction and TTL behavior.

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [cache eviction implementation part 1](./cache-eviction-implementation-part-1.md)
- [cache eviction implementation part 2](./cache-eviction-implementation-part-2.md)
