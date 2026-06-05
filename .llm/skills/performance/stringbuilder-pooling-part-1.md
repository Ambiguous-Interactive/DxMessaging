---
title: "StringBuilder Pooling for Zero-Allocation String Building Part 1"
id: "stringbuilder-pooling-part-1"
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

Continuation material extracted from `stringbuilder-pooling.md` to keep .llm files within the 300-line budget.

## Solution

## Usage

### Basic Usage

```csharp
public string BuildItemList(List<Item> items)
{
    using var lease = Buffers.StringBuilder.Get(out StringBuilder sb, items.Count * 20);

    for (int i = 0; i < items.Count; i++)
    {
        sb.Append(items[i].Name);
        if (i < items.Count - 1)
        {
            sb.Append(", ");
        }
    }

    return sb.ToString();
} // sb.Clear() + return to pool
```

### Formatting with Values

```csharp
public string FormatStats(Player player)
{
    using var lease = Buffers.StringBuilder.Get(out StringBuilder sb, 128);

    sb.Append("HP: ").Append(player.Health).Append('/').Append(player.MaxHealth);
    sb.AppendLine();
    sb.Append("MP: ").Append(player.Mana).Append('/').Append(player.MaxMana);
    sb.AppendLine();
    sb.Append("Level: ").Append(player.Level);

    return sb.ToString();
}
```

### JSON-like Building

```csharp
public string ToJson(Dictionary<string, object> data)
{
    using var lease = Buffers.StringBuilder.Get(out StringBuilder sb, 512);

    sb.Append('{');
    bool first = true;

    foreach (KeyValuePair<string, object> kvp in data)
    {
        if (!first) sb.Append(',');
        first = false;

        sb.Append('"').Append(kvp.Key).Append("\":");

        if (kvp.Value is string str)
        {
            sb.Append('"').Append(str).Append('"');
        }
        else
        {
            sb.Append(kvp.Value);
        }
    }

    sb.Append('}');
    return sb.ToString();
}
```

## Performance Notes

- **Allocations**: Zero in steady state; only `ToString()` allocates (unavoidable)
- **Capacity Hints**: Providing accurate hints avoids internal resizing
- **Memory**: Pool auto-trims oversized builders (>8KB default)
- **Timing**: O(1) pool operations

### Benchmark Comparison

| Approach             | 100 items | Allocations      |
| -------------------- | --------- | ---------------- |
| String +=            | 15.2ms    | 199 strings      |
| New StringBuilder    | 0.8ms     | 1 StringBuilder  |
| Pooled StringBuilder | 0.8ms     | 0 (steady state) |

## Best Practices

### Do

- Estimate capacity: `items.Count * avgItemLength`
- Use `Append()` chains for readability
- Pool StringBuilders used in Update loops
- Call `ToString()` only once at the end

### Don't

- Don't call `ToString()` multiple times (creates copies)
- Don't store StringBuilder references beyond the using scope
- Don't forget the capacity hint for large strings
- Don't pool for one-time operations (allocation amortization not worth it)

## Variations

### Thread-Local StringBuilder

For multithreaded code without locking:

```csharp
[ThreadStatic]
private static StringBuilder threadLocalBuilder;

public static StringBuilder GetThreadLocal()
{
    StringBuilder sb = threadLocalBuilder ?? (threadLocalBuilder = new StringBuilder(256));
    sb.Clear();
    return sb;
}
```

## Related Patterns

- [Collection Pooling](./collection-pooling.md) - General collection pooling

## See Also

- [StringBuilder Pooling for Zero-Allocation String Building](./stringbuilder-pooling.md)
