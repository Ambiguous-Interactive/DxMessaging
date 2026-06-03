---
title: "Object Pooling Anti-Patterns"
id: "object-pooling-anti-patterns"
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

# Object Pooling Anti-Patterns

> **One-line summary**: Common mistakes to avoid when using pooled objects.

## Overview

This skill documents common mistakes that break pooling safety or correctness.

## Solution

Avoid the anti-patterns below and follow the safer alternatives.

## Anti-Patterns

### Don't Hold References to Pooled Objects

```csharp
public class BadHandler
{
    private DamageMessage lastDamage; // WRONG: Holding pooled object

    public void OnDamage(DamageMessage message)
    {
        lastDamage = message; // This reference becomes invalid after handler returns!
    }
}
```

**Why it's wrong**: The pooled object will be reset and reused. Your reference will contain stale or corrupted data.

**Fix**: Copy the data you need:

```csharp
public class GoodHandler
{
    private int lastDamageAmount;
    private InstanceId lastDamageSource;

    public void OnDamage(DamageMessage message)
    {
        lastDamageAmount = message.Damage;
        lastDamageSource = message.Source;
    }
}
```

### Don't Forget to Return Objects

```csharp
public void ProcessDamage()
{
    DamageMessage message = DamageMessage.Rent();
    message.Damage = 10;

    if (SomeCondition())
    {
        return; // WRONG: message leaked, becomes garbage
    }

    messageBus.Emit(message);
    message.Dispose();
}
```

**Fix**: Use try/finally or `using`:

```csharp
public void ProcessDamage()
{
    using (DamageMessage message = DamageMessage.Rent())
    {
        message.Damage = 10;

        if (SomeCondition())
        {
            return; // Safe: Dispose called automatically
        }

        messageBus.Emit(message);
    }
}
```
