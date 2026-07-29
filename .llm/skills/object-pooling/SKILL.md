---
name: object-pooling
description: "Reuse pooled message objects through Rent/Return instead of allocating a new instance per emit, so dispatch stays allocation-free. Use when adding a message type, seeing GC spikes or a rising allocation rate in the Unity profiler, writing an allocation test, or choosing between a pooled class message and a readonly struct message."
metadata:
  category: "performance"
  tags: "memory, allocation, garbage-collection, pooling, zero-alloc, hot-path"
---

# Object Pooling for Zero-Allocation Messaging

Pre-allocate message instances and reuse them instead of calling `new` per emit.
Messages are high-frequency, short-lived objects, so their allocations dominate
GC pressure and produce visible frame spikes.

## When to use

- Adding a message type that will be emitted more than ~100 times per second.
- The profiler shows a high allocation rate or `GC.Collect` inside messaging code.
- Frame rate drops every few seconds with a sawtooth memory graph.
- Targeting mobile or console, or holding a consistent 60+ FPS budget.
- Choosing between a pooled class message and a `readonly struct` message.

Skip pooling for editor tools, one-shot events (level start, game over), and
low-frequency UI events. The pool bookkeeping is not free.

## Rules

- Acquire with `Rent()`, never `new`. Release with `Dispose()` or `Return(item)`.
- Wrap every rental in `using` or `try`/`finally`. An early `return` between
  `Rent()` and `Dispose()` leaks the instance into the GC, which is the exact
  cost pooling exists to avoid.
- Reset state on return, not after rent. `ObjectPool<T>.Return` invokes the
  `resetAction` before pushing, so a stale field can never reach the next caller.
- Cap the pool. `ObjectPool<T>(initialCapacity: 16, maxSize: 1024, resetAction: null)`
  pre-warms `initialCapacity` instances at construction and drops returns once
  `pool.Count >= maxSize`, so a usage spike cannot grow memory without bound.
- `Rent()` on an empty pool allocates a fresh `T` rather than blocking. That
  instance is still poolable on return; only the first frames pay for it.
- Guard all pool state, including `CountInactive`, behind the pool's `syncLock`.
- Never hold a reference to a pooled object past the handler call. The instance
  is reset and handed to someone else. Copy the fields you need
  (`message.Damage`, `message.Source`) into your own storage instead.
- Prefer the `PooledMessage<TSelf> : IMessage, IDisposable` base for pooled
  message types: it owns the static `ObjectPool<TSelf>`, exposes `static Rent()`,
  and its `isRented` latch makes a second `Dispose()` a no-op instead of a
  double-return that hands one instance to two callers.
- `protected abstract void Reset()` on a pooled message must clear every field
  the type declares back to `default`.
- Prefer a `readonly struct` message emitted by value when the payload is small
  and the API allows it. That removes the pool bookkeeping as well as the
  allocation, and it is the fastest of the three shapes.
- Pre-warm during loading, not during gameplay. Pool construction is the only
  place allocation is acceptable.

## Measured cost

At 10,000 messages per frame (Unity 2021.3, IL2CPP):

| Approach        | Allocations/frame | GC pressure | Frame time |
| --------------- | ----------------- | ----------- | ---------- |
| `new` each time | 10,000            | 400 KB      | 2.1 ms     |
| Object pool     | 0 after warm-up   | 0 KB        | 0.8 ms     |
| Struct messages | 0                 | 0 KB        | 0.5 ms     |

## Test coverage

Every pool needs four tests: reuse identity (`ReferenceEquals(first, second)`
after a rent/return/rent cycle), the `maxSize` cap (`CountInactive` stops at
`maxSize` after over-returning), reset behavior (no field survives a round trip),
and concurrent rent/return.

## References

| Document                                                                          | Purpose                                                                                   |
| --------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| [object-pooling.md](./references/object-pooling.md)                               | Why per-emit allocation causes GC spikes, with the profiler symptom list                  |
| [object-pooling-part-1.md](./references/object-pooling-part-1.md)                 | Full `ObjectPool<T>` implementation, rent/return/clear semantics, and the pool test cases |
| [object-pooling-part-2.md](./references/object-pooling-part-2.md)                 | Benchmark table and the decision rule for when pooling pays off                           |
| [object-pooling-variations.md](./references/object-pooling-variations.md)         | `PooledMessage<TSelf>` base class and the struct-message alternative                      |
| [object-pooling-usage-examples.md](./references/object-pooling-usage-examples.md) | Rent, emit, and dispose call sites for combat and broadcast messages                      |
| [object-pooling-anti-patterns.md](./references/object-pooling-anti-patterns.md)   | Holding references to pooled objects and leaking un-returned rentals                      |
