---
title: "Object Pooling for Zero-Allocation Messaging"
id: "object-pooling"
category: "performance"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-01-21"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/Pool/ObjectPool.cs"
      lines: "1-150"
    - path: "Runtime/Core/Messages/PooledMessage.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "memory"
  - "allocation"
  - "garbage-collection"
  - "pooling"
  - "zero-alloc"
  - "hot-path"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of object lifecycle and reference management, but implementation is straightforward"

impact:
  performance:
    rating: "high"
    details: "Eliminates per-message allocations, reducing GC pressure significantly in high-throughput scenarios"
  maintainability:
    rating: "medium"
    details: "Adds complexity around object lifecycle but follows clear patterns"
  testability:
    rating: "low"
    details: "Pools are implementation details; tests focus on behavior not pooling"

prerequisites:
  - "Understanding of C# memory management"
  - "Knowledge of garbage collection impact on games"

dependencies:
  packages: []
  skills: []

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "Memory pooling"
  - "Object reuse"
  - "Flyweight pattern"

related:
  - "collection-pooling"
  - "array-pooling"
  - "object-pooling-variations"
  - "object-pooling-usage-examples"
  - "object-pooling-anti-patterns"

status: "stable"
---

# Object Pooling for Zero-Allocation Messaging

> **One-line summary**: Eliminate garbage collection spikes by reusing message objects instead of allocating new ones for each message emit.

## Overview

Object pooling is a creational pattern that pre-allocates and reuses objects instead of creating new instances each time they're needed. In messaging systems, this is critical because:

1. **High frequency**: Message systems often emit thousands of messages per frame
1. **Short-lived objects**: Messages are typically consumed immediately and discarded
1. **GC pressure**: Frequent allocations of short-lived objects trigger garbage collection
1. **Frame spikes**: GC pauses cause visible stuttering in games

By pooling message objects, we achieve near-zero allocation messaging even under heavy load.

## Problem Statement

Consider a typical messaging system that allocates a new message for each emit:

```csharp
public void EmitDamageEvent(int damage, GameObject source)
{
    // Allocates ~40 bytes per call
    DamageMessage message = new DamageMessage(damage, source);
    messageBus.Emit(message);
    // message becomes garbage immediately after handlers complete
}
```

In a game with 100 entities each emitting 10 messages per second, that's 1,000 allocations/second. Over 60 seconds, you've created 60,000 garbage objects, triggering multiple GC collections.

**Symptoms**:

- Frame rate drops every few seconds (GC spikes)
- Increasing memory usage followed by sudden drops
- Profiler shows high allocation rate in messaging code
- `GC.Collect` appearing in profiler hot path

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [object pooling part 1](./object-pooling-part-1.md)
- [object pooling part 2](./object-pooling-part-2.md)
