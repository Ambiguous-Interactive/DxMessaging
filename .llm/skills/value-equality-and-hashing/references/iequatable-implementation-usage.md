# IEquatable Usage Examples

> **One-line summary**: Using IEquatable in dictionaries, hash sets, and comparisons.

## Overview

This skill shows how to apply IEquatable in common collection scenarios.

## Solution

Use the examples below to integrate equality in dictionaries and sets.

## Usage

### In Dictionary

```csharp
var positions = new Dictionary<Point, GameObject>(256);

// All operations use typed Equals and GetHashCode - no boxing
positions[new Point(1, 2)] = playerObject;

if (positions.TryGetValue(new Point(1, 2), out GameObject go))
{
    // Found!
}
```

### In HashSet

```csharp
var visited = new HashSet<Point>(512);

// Zero allocations for these operations
visited.Add(new Point(x, y));
if (visited.Contains(currentPosition))
{
    return; // Already visited
}
```

### Equality Comparisons

```csharp
Point a = new Point(1, 2);
Point b = new Point(1, 2);
Point c = new Point(3, 4);

bool equal = a == b;     // true, uses operator ==
bool notEqual = a != c;  // true, uses operator !=
bool alsoEqual = a.Equals(b); // true, direct typed call
```
