---
title: "Object Pooling Usage Examples"
id: "object-pooling-usage-examples"
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

# Object Pooling Usage Examples

> **One-line summary**: Applied object pooling scenarios for high-frequency messaging.

## Overview

This skill provides applied object pooling examples for high-frequency messaging.

## Solution

Use the examples below as starting points for your own pools.

## Usage Examples

### Example 1: High-Frequency Combat Events

```csharp
public sealed class CombatSystem
{
    private readonly MessageBus messageBus;

    public void ProcessAttack(InstanceId attacker, InstanceId target, int baseDamage)
    {
        // Rent from pool instead of allocating
        DamageMessage message = DamageMessage.Rent();
        message.Damage = CalculateDamage(baseDamage);
        message.Source = attacker;

        // Emit to all handlers
        messageBus.Emit(target, message);

        // Return to pool (or use 'using' statement)
        message.Dispose();
    }
}
```

### Example 2: Using Statement for Automatic Return

```csharp
public void BroadcastAreaDamage(Vector3 center, float radius, int damage)
{
    using (AreaDamageMessage message = AreaDamageMessage.Rent())
    {
        message.Center = center;
        message.Radius = radius;
        message.Damage = damage;

        messageBus.Broadcast(message);
    } // Automatically returned to pool here
}
```
