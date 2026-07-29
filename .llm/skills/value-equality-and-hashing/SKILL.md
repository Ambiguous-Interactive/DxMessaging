---
name: value-equality-and-hashing
description: "Give structs typed equality with IEquatable<T>, matching GetHashCode, == and != operators, and an optional constructor-cached hash so dictionary and HashSet operations stop boxing. Use when writing a struct that will be a dictionary key or set element, when the profiler shows boxing allocations in collection lookups, or when default struct equality (reflection-based) is showing up in a hot path."
metadata:
  category: "solid"
  tags: "solid, performance, struct, equality, boxing, iequatable"
---

# Value Equality and Hashing for Structs

Default struct equality compares fields by reflection and boxes on every
`object`-typed call. Implementing `IEquatable<T>` gives collections a typed
comparison path with no boxing; caching the hash at construction removes the
repeated hash computation on top of that.

## When to use

- Declaring a struct that will be a `Dictionary` key or `HashSet` element.
- The profiler shows 24-byte allocations inside `ContainsKey`, `Contains`, or
  `TryGetValue`.
- A coordinate, id, or composite key type is looked up thousands of times a
  frame.
- Reviewing a struct that overrides `Equals(object)` but not `IEquatable<T>`.

## Rules

- Implement all four members together, never a subset: `bool Equals(T other)`,
  `override bool Equals(object obj)`, `override int GetHashCode()`, and both
  `operator ==` / `operator !=`. A missing member silently reintroduces the
  boxing path.
- `Equals(object obj)` must be `obj is T other && Equals(other)` - it forwards,
  it does not duplicate the comparison.
- Declare the type `readonly struct` so the compiler cannot emit defensive
  copies on member access.
- Put `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on `Equals(T)`,
  `GetHashCode()`, and the operators. Leave it off `Equals(object)` - it is the
  cold path.
- Compute hashes in an `unchecked` block with prime multiplication:
  `hash = 17; hash = hash * 31 + field;` per field. `HashCode.Combine(x, y)` and
  Unity's `x ^ (y << 2)` are also acceptable. Overflow is expected and fine.
- Never include a mutable field in the hash. The value must be stable for the
  lifetime of the instance or the entry is lost inside its collection.
- Handle nullable and reference fields explicitly: `(X?.GetHashCode() ?? 0)` in
  the hash, and `string.Equals(Label, other.Label, StringComparison.Ordinal)`
  for strings so equality never depends on the current culture.
- Cache the hash in a `private readonly int _hash` assigned in the constructor
  when the type is a heavily used dictionary key. `GetHashCode()` then returns
  `_hash` with no computation, and `Equals` opens with `_hash == other._hash &&`
  as an early-out before comparing fields. Do not add the cached-hash field to a
  type that is rarely hashed; the constructor cost is not repaid.
- Provide implicit conversions to and from the framework type a cached-hash
  struct replaces (for example `UnityEngine.Vector2Int`), so call sites can adopt
  it without casts.
- Preserve the equality contract: reflexive, symmetric, transitive, consistent
  across calls, and `a.Equals(b)` must imply
  `a.GetHashCode() == b.GetHashCode()`.

## Measured cost

| Operation          | Without `IEquatable<T>` | With `IEquatable<T>` |
| ------------------ | ----------------------- | -------------------- |
| Dictionary lookup  | ~50 ns + 24 B alloc     | ~10 ns, 0 alloc      |
| `HashSet.Contains` | ~30 ns + 24 B alloc     | ~5 ns, 0 alloc       |
| `List.Contains`    | ~20 ns + 24 B alloc     | ~5 ns, 0 alloc       |

Across 128,000 `HashSet.Contains` calls that is 4 MB of boxing allocations
against zero. Over 100,000 dictionary lookups, a cached-hash key measured 5.1 ms
against 8.2 ms for `Vector2Int`, 12.4 ms for `Tuple<int, int>`, and 45.3 ms plus
3.2 MB for a formatted string key.

## References

| Document                                                                                                          | Purpose                                                                                                       |
| ----------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| [iequatable-implementation.md](./references/iequatable-implementation.md)                                         | Canonical four-member `IEquatable<T>` struct, boxing costs, and the equality contract                         |
| [iequatable-implementation-variants.md](./references/iequatable-implementation-variants.md)                       | Cached-hash, nullable-field, and reference-field equality variants                                            |
| [iequatable-implementation-usage.md](./references/iequatable-implementation-usage.md)                             | Dictionary, HashSet, and operator call sites                                                                  |
| [readonly-struct-cached-hash.md](./references/readonly-struct-cached-hash.md)                                     | Why recomputing a hash per lookup and boxing in `Equals(object)` costs throughput                             |
| [readonly-struct-cached-hash-part-1.md](./references/readonly-struct-cached-hash-part-1.md)                       | `FastVector2Int` and `FastVector3Int` implementations with constructor-computed hash and implicit conversions |
| [readonly-struct-cached-hash-part-2.md](./references/readonly-struct-cached-hash-part-2.md)                       | Key and set usage, hash-quality options, and the do/do-not list                                               |
| [readonly-struct-cached-hash-performance-notes.md](./references/readonly-struct-cached-hash-performance-notes.md) | 100,000-lookup benchmark across key types and why the cached hash wins                                        |
