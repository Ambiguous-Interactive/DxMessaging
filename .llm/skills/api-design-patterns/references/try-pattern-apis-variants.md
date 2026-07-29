# Try-Pattern API Variants

> **One-line summary**: Dictionary, TryParse, and component-style variants.

## Overview

This skill compares common Try-pattern API shapes.

## Solution

Pick the variant that matches your call site and error semantics.

### Dictionary-Style TryGet

```csharp
public sealed class Cache<TKey, TValue>
{
    private readonly Dictionary<TKey, CacheEntry> entries;

    /// <summary>
    /// Attempts to retrieve a value from the cache.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value if found, default otherwise.</param>
    /// <returns>True if the key exists and the value was retrieved.</returns>
    public bool TryGet(TKey key, out TValue value)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (entries.TryGetValue(key, out CacheEntry entry))
        {
            if (!IsExpired(entry))
            {
                UpdateAccessTime(entry);
                value = entry.Value;
                return true;
            }

            // Expired - remove and return false
            entries.Remove(key);
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets a value from the cache, or computes and caches it.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        if (TryGet(key, out TValue existing))
        {
            return existing;
        }

        TValue newValue = valueFactory(key);
        Put(key, newValue);
        return newValue;
    }
}
```

### Parse-Style TryParse

```csharp
public static class ColorParser
{
    /// <summary>
    /// Attempts to parse a hex color string.
    /// </summary>
    /// <param name="hex">Hex string like "#FF0000" or "FF0000".</param>
    /// <param name="color">Parsed color if successful.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseHex(string hex, out Color color)
    {
        color = default;

        if (string.IsNullOrEmpty(hex))
        {
            return false;
        }

        // Strip leading #
        if (hex[0] == '#')
        {
            hex = hex.Substring(1);
        }

        if (hex.Length != 6 && hex.Length != 8)
        {
            return false;
        }

        if (!TryParseHexByte(hex, 0, out byte r)) return false;
        if (!TryParseHexByte(hex, 2, out byte g)) return false;
        if (!TryParseHexByte(hex, 4, out byte b)) return false;

        byte a = 255;
        if (hex.Length == 8)
        {
            if (!TryParseHexByte(hex, 6, out a)) return false;
        }

        color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        return true;
    }

    private static bool TryParseHexByte(string hex, int offset, out byte value)
    {
        if (byte.TryParse(
            hex.Substring(offset, 2),
            System.Globalization.NumberStyles.HexNumber,
            null,
            out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Parses a hex color or returns a default.
    /// </summary>
    public static Color ParseHexOrDefault(string hex, Color defaultColor = default)
    {
        return TryParseHex(hex, out Color color) ? color : defaultColor;
    }
}
```

### Component-Style TryGet

```csharp
public static class GameObjectExtensions
{
    /// <summary>
    /// Attempts to get a component of the specified type.
    /// </summary>
    /// <remarks>
    /// <para>Performance: O(n) where n is number of components.</para>
    /// <para>Allocations: No allocations in Unity 2019.3+.</para>
    /// </remarks>
    public static bool TryGetComponentSafe<T>(this GameObject go, out T component)
        where T : class
    {
        if (go == null)
        {
            component = null;
            return false;
        }

        return go.TryGetComponent(out component);
    }

    /// <summary>
    /// Gets a component or adds it if missing.
    /// </summary>
    public static T GetOrAddComponent<T>(this GameObject go) where T : Component
    {
        if (go.TryGetComponent(out T component))
        {
            return component;
        }

        return go.AddComponent<T>();
    }
}
```
