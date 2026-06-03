---
title: "Object Pooling Variations"
id: "object-pooling-variations"
category: "performance"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-01-21"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "memory"
  - "allocation"
  - "pooling"
  - "messaging"

complexity:
  level: "intermediate"
  reasoning: "Expands on object pooling details and trade-offs"

impact:
  performance:
    rating: "high"
    details: "Pooled objects reduce GC pressure in hot paths"
  maintainability:
    rating: "medium"
    details: "Requires disciplined lifecycle management"
  testability:
    rating: "medium"
    details: "Pooling patterns need explicit tests for reuse"

prerequisites:
  - "Understanding of object pooling"

dependencies:
  packages: []
  skills:
    - "object-pooling"

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
  - "Object pooling details"

related:
  - "object-pooling"

status: "stable"
---

# Object Pooling Variations

> **One-line summary**: Variations on object pooling for messaging and structs.

## Overview

This skill compares variations on pooled message and struct-based approaches.

## Solution

Choose the variant that matches allocation goals and API constraints.

## Variations

### Variation A: Pooled Message Base Class

Create a base class that handles pool return automatically:

```csharp
public abstract class PooledMessage<TSelf> : IMessage, IDisposable
    where TSelf : PooledMessage<TSelf>, new()
{
    private static readonly ObjectPool<TSelf> Pool = new ObjectPool<TSelf>(
        initialCapacity: 32,
        resetAction: m => m.Reset()
    );

    private bool isRented;

    public static TSelf Rent()
    {
        TSelf instance = Pool.Rent();
        instance.isRented = true;
        return instance;
    }

    public void Dispose()
    {
        if (isRented)
        {
            isRented = false;
            Pool.Return((TSelf)this);
        }
    }

    /// <summary>
    /// Override to clear instance-specific state.
    /// </summary>
    protected abstract void Reset();
}

// Usage
public sealed class DamageMessage : PooledMessage<DamageMessage>
{
    public int Damage { get; set; }
    public InstanceId Source { get; set; }

    protected override void Reset()
    {
        Damage = 0;
        Source = default;
    }
}
```

### Variation B: Struct-Based Zero-Alloc

For maximum performance, use structs to avoid even pool overhead:

```csharp
public readonly struct DamageEvent : IMessage
{
    public readonly int Damage;
    public readonly InstanceId Source;

    public DamageEvent(int damage, InstanceId source)
    {
        Damage = damage;
        Source = source;
    }
}

// Emit by value - no allocation at all
messageBus.Emit(new DamageEvent(10, attackerId));
```
