---
name: api-design-patterns
description: "Allocation-conscious C# API and collection shapes used across DxMessaging and its helper libraries: struct-based fluent builders, Try-pattern APIs, concrete collection fast paths, and allocation-safe enumeration and sorting. Use when designing these APIs, replacing exceptions or LINQ on a hot path, or optimizing analyzer/source-generator collection loops and deterministic ordering."
metadata:
  category: "solid"
  tags: "solid, patterns, builder, fluent-api, zero-alloc"
---

# API Design Patterns

Three API shapes for code that runs per-frame in Unity: struct fluent builders, Try-pattern
accessors, and documented collection extensions. All three trade a little extra code for zero
heap allocation and a call site that reads like its intent.

## When to use

- A constructor has more than three parameters, or unlabeled `true` / `null` arguments.
- Designing an operation that can fail for ordinary reasons (missing key, out-of-range index,
  malformed input).
- Adding an extension method on `IReadOnlyList<T>`, `IList<T>`, or `IEnumerable<T>`.
- Removing LINQ or exception-driven control flow from a hot path.
- Optimizing an analyzer or source generator that owns a collection pipeline or sorts symbols.

## Rules

### Struct fluent builders

- Declare the builder as a `struct`, not a `class`, so construction allocates nothing.
- Every `With*` method copies the receiver and returns the copy:
  `var copy = this; copy.field = value; copy.initialized = true; return copy;`. Builders are
  never mutated in place, so a partially configured builder can be stored and branched from.
- Validate a single argument eagerly inside its `With*` method (throw
  `ArgumentOutOfRangeException` / `ArgumentNullException`). Validate combinations in `Build()`
  and throw `InvalidOperationException` with a message naming the fix ("Jitter requires
  expiration to be set. Call WithExpireAfterWrite or WithExpireAfterAccess first.").
- Carry an explicit `initialized` or per-field `...Set` bool so `Build()` can distinguish "not
  set" from "set to the default value", and apply defaults in `Build()`.
- Keep struct builders under roughly 10 fields and 200 bytes; beyond that a class builder is
  cheaper than the per-chain copy. Do not store reference types that the caller can mutate
  after `Build()`.
- Provide a static factory entry point (`Cache.Builder<TKey, TValue>()`) so the generic
  arguments can be inferred at the call site.

### Try-pattern APIs

- Signature is `bool TryX(..., out T value)`. Return `false` and set `value = default` on every
  failure path; never throw for an expected failure.
- Keep a throwing variant only for programming errors, implemented on top of the Try method,
  and throw `ArgumentOutOfRangeException` with the parameter name, the value, and the valid
  range.
- Pair each `TryX` with a `GetOrDefault(..., T defaultValue = default)` convenience overload.
- Argument-contract violations still throw: a null key passed to `TryGet` is
  `ArgumentNullException`, not `false`.
- Bounds-check with the single-comparison idiom `(uint)index < (uint)count`.
- Mark tiny Try methods `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Do not return `null` to signal "missing" when `T` can legitimately be null or when `default`
  is a valid value (`0` for `int`).
- Use exceptions only for genuinely exceptional conditions (I/O failure, invalid arguments).
  A failing Try call costs about the same as a successful one; a thrown exception costs orders
  of magnitude more, and capturing a stack trace more again.

### Collection extensions

- Document complexity, allocation, and thread safety in `<remarks>` with three `<para>` lines:
  `Performance: O(...)`, `Allocations: ...`, `Thread Safety: ...`. Never hide an allocation.
- Throw `ArgumentNullException(nameof(list))` on a null receiver. `IsNullOrEmpty` is the
  exception: it answers the question rather than throwing, and short-circuits through
  `ICollection<T>` then `IReadOnlyCollection<T>` before falling back to an enumerator.
- Provide concrete-type fast paths before the interface fallback (`if (list is T[] array)`,
  `if (list is List<T> concreteList)`) for operations on hot paths; interface dispatch costs
  roughly 5 ns per access versus 0.5 ns direct.
- Do not use LINQ on hot paths; `FirstOrDefault` and friends allocate an enumerator.
- Shuffle in place with Fisher-Yates and offer a `System.Random` overload so tests can be
  deterministic. Assert on permutations by comparing sorted sequences.

### Hot collection pipelines

- Preserve a concrete collection type when the implementation creates and owns it. In particular,
  passing a `List<T>` as `IReadOnlyList<T>` and using `foreach` calls the interface
  `GetEnumerator()`; runtimes used by Roslyn and Unity can box the list's struct enumerator. Either
  keep `List<T>` in the private pipeline or use an indexed loop when abstraction is required.
- Judge `foreach` from the expression's **static type**, not its runtime object. Concrete
  `List<T>`, array, and `ImmutableArray<T>` loops have allocation-free lowering; interface-typed
  `IEnumerable<T>`, `IReadOnlyCollection<T>`, and `IReadOnlyList<T>` loops may not.
- Validate allocation claims on the code's actual host/runtime. A Unity runtime observation does
  not prove how a Roslyn analyzer hosted by the compiler behaves. Use fresh-process repetitions,
  warm both variants equally, retain checksums or generated-output equality, and report the tested
  runtime.
- Materialize allocation-producing sort keys once per item. Never call `ToDisplayString`, format a
  string, normalize a path, or perform another allocating transformation inside a comparison:
  sorting invokes the comparison O(n log n) times.
- Prefer a reusable singleton `IComparer<T>` or a demonstrably cached non-capturing comparison for
  repeated sorts. A non-allocating delegate does not make an allocating comparison body safe.
- Pair ordering optimizations with a behavior test that pins deterministic ordinal output. Sweep
  every production sort and interface-typed enumeration in the affected analyzer/generator scope;
  do not mechanically rewrite UI/reporting LINQ outside the measured hot path.

## References

| Document                                                                                                  | Purpose                                                                                                             |
| --------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| [collection-extensions-accessors.md](./references/collection-extensions-accessors.md)                     | TryGetFirst/TryGetLast/GetRandom/TryGetRandom and the IsNullOrEmpty overload set                                    |
| [collection-extensions-shuffle.md](./references/collection-extensions-shuffle.md)                         | In-place Fisher-Yates shuffle with an injectable System.Random for deterministic tests                              |
| [collection-extensions-type-specialization.md](./references/collection-extensions-type-specialization.md) | Concrete-type fast paths versus interface dispatch, with a BinarySearch example                                     |
| [collection-extensions.md](./references/collection-extensions.md)                                         | The performance-documentation XML template and the do/do-not list for extension methods                             |
| [fluent-builder-pattern-part-1.md](./references/fluent-builder-pattern-part-1.md)                         | Full CacheBuilder struct implementation and the struct-versus-class allocation comparison                           |
| [fluent-builder-pattern-part-2.md](./references/fluent-builder-pattern-part-2.md)                         | Builder do/do-not list and the initialized-flag pattern for distinguishing unset from default                       |
| [fluent-builder-pattern-templates.md](./references/fluent-builder-pattern-templates.md)                   | Static factory entry point and a generic ObjectBuilder template                                                     |
| [fluent-builder-pattern-usage-examples.md](./references/fluent-builder-pattern-usage-examples.md)         | Call-site examples: basic chaining, static factory, reused partial configuration, Build-time validation             |
| [fluent-builder-pattern.md](./references/fluent-builder-pattern.md)                                       | Why struct builders exist and the many-parameter constructor problem they replace                                   |
| [try-pattern-apis-usage.md](./references/try-pattern-apis-usage.md)                                       | Call-site examples: basic Try usage, chained Try operations, action-on-success                                      |
| [try-pattern-apis-variants.md](./references/try-pattern-apis-variants.md)                                 | Dictionary-style TryGet with expiry, parse-style TryParseHex, and component-style TryGetComponent/GetOrAddComponent |
| [try-pattern-apis.md](./references/try-pattern-apis.md)                                                   | The core Try shape, exception-versus-Try cost table, and when to throw instead                                      |
