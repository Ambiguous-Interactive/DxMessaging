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
