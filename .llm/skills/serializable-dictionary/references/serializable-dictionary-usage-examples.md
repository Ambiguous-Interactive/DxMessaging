# Serializable Dictionary Usage Examples

> **One-line summary**: Usage examples for ScriptableObjects and enum keys.

## Overview

This skill provides common serialization setups for dictionary usage.

## Solution

Apply the examples below for ScriptableObjects and enum-keyed dictionaries.

## Usage

### Basic Usage

```csharp
public class LootTable : MonoBehaviour
{
    [SerializeField]
    private SerializableDictionary<string, float> dropRates =
        new SerializableDictionary<string, float>();

    public float GetDropRate(string itemId)
    {
        return dropRates.TryGetValue(itemId, out float rate) ? rate : 0f;
    }
}
```

### With ScriptableObject

```csharp
[CreateAssetMenu(menuName = "Config/Enemy Stats")]
public class EnemyStatsConfig : ScriptableObject
{
    [SerializeField]
    private SerializableDictionary<string, EnemyStats> enemyStats;

    public EnemyStats GetStats(string enemyType)
    {
        return enemyStats.TryGetValue(enemyType, out EnemyStats stats)
            ? stats
            : EnemyStats.Default;
    }
}

[Serializable]
public struct EnemyStats
{
    public int health;
    public float speed;
    public int damage;

    public static EnemyStats Default => new EnemyStats { health = 100, speed = 1f, damage = 10 };
}
```

### Enum Keys

```csharp
public enum DamageType { Physical, Fire, Ice, Lightning }

public class DamageResistances : MonoBehaviour
{
    [SerializeField]
    private SerializableDictionary<DamageType, float> resistances;

    public float GetResistance(DamageType type)
    {
        return resistances.TryGetValue(type, out float resistance) ? resistance : 0f;
    }
}
```
