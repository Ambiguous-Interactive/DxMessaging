# Fluent Builder Templates and Factories

> **One-line summary**: Static factory entry points and reusable builder templates.

## Overview

This skill provides reusable templates and entry points for builders.

## Solution

Use these templates to standardize fluent builder APIs.

### Static Factory Entry Point

```csharp
/// <summary>
/// Entry point for cache creation.
/// </summary>
public static class Cache
{
    /// <summary>
    /// Creates a new cache builder.
    /// </summary>
    public static CacheBuilder<TKey, TValue> Builder<TKey, TValue>()
    {
        return new CacheBuilder<TKey, TValue>();
    }
}
```

### Generic Builder Template

```csharp
/// <summary>
/// Template for struct-based builders.
/// </summary>
public struct ObjectBuilder<T> where T : class, new()
{
    private T prototype;

    private T GetOrCreatePrototype()
    {
        return prototype ?? (prototype = new T());
    }

    public ObjectBuilder<T> With(Action<T> configure)
    {
        var copy = this;
        configure(copy.GetOrCreatePrototype());
        return copy;
    }

    public T Build()
    {
        return prototype ?? new T();
    }
}
```
