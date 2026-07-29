# IEquatable Implementation Variants

> **One-line summary**: Extensions for cached hashes, nullable fields, and reference comparisons.

## Overview

This skill documents IEquatable variants for specialized needs.

## Solution

Select the variant that matches hashing or null-handling requirements.

### With Cached Hash (for heavy dictionary usage)

```csharp
public readonly struct FastPoint : IEquatable<FastPoint>
{
    public readonly int X;
    public readonly int Y;
    private readonly int _hash;

    public FastPoint(int x, int y)
    {
        X = x;
        Y = y;
        unchecked
        {
            _hash = 17;
            _hash = _hash * 31 + x;
            _hash = _hash * 31 + y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FastPoint other)
    {
        // Early-out on hash mismatch
        return _hash == other._hash && X == other.X && Y == other.Y;
    }

    public override bool Equals(object obj)
    {
        return obj is FastPoint other && Equals(other);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _hash;

    public static bool operator ==(FastPoint left, FastPoint right) => left.Equals(right);
    public static bool operator !=(FastPoint left, FastPoint right) => !left.Equals(right);
}
```

### With Nullable Fields

```csharp
public readonly struct OptionalPoint : IEquatable<OptionalPoint>
{
    public readonly int? X;
    public readonly int? Y;
    public readonly string Label; // Reference type

    public OptionalPoint(int? x, int? y, string label)
    {
        X = x;
        Y = y;
        Label = label;
    }

    public bool Equals(OptionalPoint other)
    {
        return X == other.X
            && Y == other.Y
            && string.Equals(Label, other.Label, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is OptionalPoint other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (X?.GetHashCode() ?? 0);
            hash = hash * 31 + (Y?.GetHashCode() ?? 0);
            hash = hash * 31 + (Label?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(OptionalPoint left, OptionalPoint right) => left.Equals(right);
    public static bool operator !=(OptionalPoint left, OptionalPoint right) => !left.Equals(right);
}
```

### With Reference Type Comparisons

```csharp
public readonly struct Entity : IEquatable<Entity>
{
    public readonly int Id;
    public readonly string Name;
    public readonly Type ComponentType;

    public Entity(int id, string name, Type componentType)
    {
        Id = id;
        Name = name;
        ComponentType = componentType;
    }

    public bool Equals(Entity other)
    {
        return Id == other.Id
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && ComponentType == other.ComponentType;
    }

    public override bool Equals(object obj)
    {
        return obj is Entity other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Id;
            hash = hash * 31 + (Name?.GetHashCode() ?? 0);
            hash = hash * 31 + (ComponentType?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);
    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
```
