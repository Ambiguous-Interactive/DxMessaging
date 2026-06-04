---
title: "Serializable Dictionary for Unity Inspector Part 1"
id: "serializable-dictionary-part-1"
category: "performance"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-03-16"
status: "stable"
tags:
  - migration
  - split
complexity:
  level: "intermediate"
impact:
  performance:
    rating: "low"
---

## Overview

Continuation extracted from `serializable-dictionary.md` to keep files within the repository line-budget policy.

## Solution

## Best Practices

### Do

- Use for configuration data edited in Inspector
- Use with ScriptableObjects for shared config
- Keep dictionaries reasonably sized (<1000 entries for editor performance)
- Use meaningful key types (enums, strings)

### Don't

- Don't use for runtime-generated data (no serialization benefit)
- Don't use for very large datasets (serialization overhead)
- Don't duplicate keys in Inspector (later one wins)
- Don't store UnityEngine.Object references that might become null

### Handling Duplicates

```csharp
public void OnAfterDeserialize()
{
    dictionary.Clear();

    HashSet<TKey> seen = new HashSet<TKey>();
    for (int i = 0; i < keys.Count; i++)
    {
        TKey key = keys[i];
        if (key != null && seen.Add(key)) // Skip duplicates
        {
            dictionary[key] = values[i];
        }
        else if (key != null)
        {
            Debug.LogWarning($"Duplicate key ignored: {key}");
        }
    }
}
```

## Related Patterns

- [ScriptableObject Singleton Pattern](./singleton-scriptableobject.md) - Configuration with SO
- [Serializable Dictionary Property Drawer](./serializable-dictionary-property-drawer.md) - Inspector UI
- [Serializable Dictionary Usage Examples](./serializable-dictionary-usage-examples.md) - Common setups

## See Also

- [Serializable Dictionary for Unity Inspector](./serializable-dictionary.md)
