# Try-Pattern API Usage Examples

> **One-line summary**: Usage patterns for chaining, callbacks, and error handling.

## Overview

This skill focuses on Try-pattern usage in real flows.

## Solution

Apply the examples below to chain and compose Try-pattern calls.

## Usage

### Basic Try-Pattern Usage

```csharp
// Collection access
if (items.TryGet(index, out Item item))
{
    ProcessItem(item);
}
else
{
    HandleMissingItem(index);
}

// With default fallback
Item item = items.GetOrDefault(index, Item.Empty);

// Cache lookup
if (cache.TryGet(playerId, out PlayerData data))
{
    UpdateUI(data);
}
else
{
    StartDataLoad(playerId);
}
```

### Chained Try Operations

```csharp
public bool TryLoadPlayerWeapon(string playerId, out Weapon weapon)
{
    weapon = null;

    if (!playerCache.TryGet(playerId, out PlayerData player))
    {
        return false;
    }

    if (!weaponCache.TryGet(player.WeaponId, out weapon))
    {
        return false;
    }

    return true;
}
```

### Try with Action on Success

```csharp
// Extension for common pattern
public static void IfPresent<T>(this bool found, T value, Action<T> action)
{
    if (found)
    {
        action(value);
    }
}

// Usage
cache.TryGet(key, out var value).IfPresent(value, v => Process(v));
```
