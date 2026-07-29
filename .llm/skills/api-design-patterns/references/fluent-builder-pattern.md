# Fluent Builder Pattern with Struct Builders

> **One-line summary**: Implement the builder pattern using structs to provide a zero-allocation fluent API for complex object construction.

## Overview

The builder pattern separates object construction from its representation. Using a struct builder:

1. **Zero allocation** for the builder itself
1. **Fluent API** with method chaining
1. **Immutable result** - builder returns configured object
1. **Validation** in Build() method

## Problem Statement

```csharp
// BAD: Constructor with many parameters
var cache = new Cache<string, int>(
    1000,           // What is this?
    EvictionPolicy.Lru,
    TimeSpan.FromMinutes(5),
    TimeSpan.FromMinutes(1),
    true,           // What does true mean?
    null);

// BAD: Class-based builder allocates
var builder = new CacheBuilder<string, int>(); // Heap allocation!
builder.WithMaxSize(1000);
var cache = builder.Build();
```

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [fluent builder pattern part 1](./fluent-builder-pattern-part-1.md)
- [fluent builder pattern part 2](./fluent-builder-pattern-part-2.md)
