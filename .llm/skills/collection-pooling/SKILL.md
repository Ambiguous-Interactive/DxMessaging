---
name: collection-pooling
description: "Rent Lists, HashSets, Stacks, Queues, arrays, StringBuilders, and Unity yield instructions from pools with using-scoped leases so per-frame code allocates nothing. Use when writing an Update/FixedUpdate helper, a temporary buffer, a string builder, a network or serialization buffer, or a coroutine that yields WaitForSeconds."
metadata:
  category: "performance"
  tags: "memory, allocation, pooling, zero-alloc, collections, raii, disposable"
---

# Collection, Array, StringBuilder, and Yield Pooling

Temporary collections are the most common source of per-frame allocation in
Unity code. Rent them from a pool through a `using`-scoped lease that clears the
instance and returns it when the scope exits.

## When to use

- Building a temporary `List`, `HashSet`, `Stack`, or `Queue` inside
  `Update`, `FixedUpdate`, `LateUpdate`, or any per-emit code.
- Allocating a byte buffer for serialization, networking, or texture data.
- Concatenating strings in a loop, or formatting text every frame.
- Writing a coroutine that yields `new WaitForSeconds(x)`.
- Reviewing a diff that adds `new List<...>()` or `new byte[...]` to a hot path.

## Rules

- Take the lease in a `using` statement, for example
  `using var lease = Buffers<T>.List.Get(out List<T> items);`. `Dispose()`
  clears the collection and returns it; a manual `Return` on top of the lease
  double-returns it.
- Never let a pooled collection, array, or `StringBuilder` escape the `using`
  scope. Storing it in a field hands live state to the next renter.
- Do not share a `PooledResource` or `PooledArray` across threads, and prefer
  per-thread pools for multithreaded code.
- Do not pool objects with finalizers or native resources.
- Pick the array pool by requirement, and never return an array to a pool it did
  not come from:
  - `WallstopArrayPool<T>` - exact size, clears on return (~50 ns), for
    credentials, keys, and any data that must not leak into the next renter.
  - `WallstopFastArrayPool<T> where T : unmanaged` - exact size, no clear
    (~10 ns), the default for numeric and pixel buffers. Callers must not assume
    the array is zeroed.
  - `SystemArrayPool<T>` - wraps `ArrayPool<T>.Shared`, may return an array
    LARGER than requested. Track the requested length yourself and copy out the
    exact bytes before returning.
- Never call `ArrayPool<T>.Shared.Rent`/`Return` on the DxMessaging dispatch hot
  path: its `Interlocked` operations are expensive under IL2CPP. Use a private
  bus-owned pool or `DxPools` there (see `dispatch-hot-path`).
- Arrays over 85 KB land on the Large Object Heap. Pool them. Do not pool arrays
  under ~64 bytes; the bookkeeping exceeds the benefit.
- Give `Buffers.StringBuilder.Get(out sb, capacityHint)` an accurate capacity
  hint (`items.Count * avgItemLength`) so the builder never resizes internally.
  Call `ToString()` exactly once, at the end; each call copies. The return path
  clears the builder and trims any builder over 8192 capacity back to 256.
- Never `yield return new WaitForSeconds(x)`. Use `Buffers.GetWaitForSeconds(x)`
  or `Buffers.GetWaitForSecondsRealtime(x)`, which quantize the duration by
  `Buffers.WaitInstructionQuantizationStepSeconds` (default `0.05f`) and cache
  one instance per quantized value. Use the `Buffers.WaitForEndOfFrame` and
  `Buffers.WaitForFixedUpdate` static singletons for those two instructions.
- Do not cache unique random durations as yield instructions; that grows the
  cache without ever hitting it. Use
  `Buffers.TryGetWaitForSecondsPooled(seconds, maxCacheSize)` when the duration
  is dynamic - it returns `null` at capacity so the caller can allocate one
  temporary instead of bloating the cache. Call `Buffers.ClearYieldCaches()` on
  scene transitions when memory is tight.
- `WaitForSecondsRealtime` instances are not thread-safe; do not share them
  across threads.

## Cost

Steady-state rent/return is O(1) and allocation-free after warm-up; the pool
grows to its high-water mark and stabilizes. String concatenation with `+=`
across 100 items costs 15.2 ms and 199 temporary strings; a pooled
`StringBuilder` costs 0.8 ms and zero steady-state allocations. Each cached
`WaitForSeconds` entry is about 20 bytes.

## References

| Document                                                                                | Purpose                                                                                               |
| --------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| [collection-pooling.md](./references/collection-pooling.md)                             | The rent/clear/return lifecycle and the per-frame `new List` problem it replaces                      |
| [collection-pooling-part-1.md](./references/collection-pooling-part-1.md)               | `PooledResource<T>` and `Buffers<T>` implementations plus nested-lease usage                          |
| [collection-pooling-part-2.md](./references/collection-pooling-part-2.md)               | Steady-state cost profile and the do/do-not list for collection leases                                |
| [array-pooling.md](./references/array-pooling.md)                                       | Comparison table of the three array pool types and when each applies                                  |
| [array-pooling-part-1.md](./references/array-pooling-part-1.md)                         | `PooledArray<T>`, `WallstopArrayPool`, `WallstopFastArrayPool`, and `SystemArrayPool` implementations |
| [array-pooling-part-2.md](./references/array-pooling-part-2.md)                         | Per-pool overhead numbers, Large Object Heap guidance, and mixing rules                               |
| [array-pooling-usage-examples.md](./references/array-pooling-usage-examples.md)         | Network buffer, serialization, secure data, and texture filter call sites                             |
| [stringbuilder-pooling.md](./references/stringbuilder-pooling.md)                       | Why `+=` concatenation is quadratic and the pooled `StringBuilder` implementation                     |
| [stringbuilder-pooling-part-1.md](./references/stringbuilder-pooling-part-1.md)         | Capacity-hint usage, benchmark comparison, and the thread-local variant                               |
| [yield-instruction-pooling.md](./references/yield-instruction-pooling.md)               | The per-coroutine `WaitForSeconds` allocation and its garbage rate                                    |
| [yield-instruction-pooling-part-1.md](./references/yield-instruction-pooling-part-1.md) | Quantized `WaitForSeconds` cache, realtime variant, and `ClearYieldCaches`                            |
| [yield-instruction-pooling-part-2.md](./references/yield-instruction-pooling-part-2.md) | Coroutine call sites, cache growth table, and quantization trade-offs                                 |
