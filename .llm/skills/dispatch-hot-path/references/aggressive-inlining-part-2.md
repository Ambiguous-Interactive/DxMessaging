## Overview

Continuation material extracted from `aggressive-inlining.md` to keep .llm files within the 300-line budget.

## Solution

## Usage Guidelines

### Good Candidates for AggressiveInlining

```csharp
// Yes Property getters (trivial)
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public int Count => _count;

// Yes Simple arithmetic/logic
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static int Clamp(int value, int min, int max)
{
    return value < min ? min : (value > max ? max : value);
}

// Yes Hash/equality operations
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public override int GetHashCode() => _cachedHash;

// Yes Type checks
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public bool IsValid => _data != null;

// Yes Forwarding calls
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public T Get(int index) => _array[index];
```

### Poor Candidates

```csharp
// No Large method bodies
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void ProcessData()
{
    // 50+ lines of code
    // Inlining this everywhere bloats code size
}

// No Virtual methods (can't inline)
[MethodImpl(MethodImplOptions.AggressiveInlining)] // Ignored
public virtual void Update() { }

// No Methods with try-catch
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void DoSomething()
{
    try { /* ... */ }
    catch { /* ... */ }
}

// No Recursive methods
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

// No Cold paths (rarely called)
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void HandleError() { /* called 0.01% of time */ }
```

## Best Practices

### Do

- Apply to property getters and simple accessors
- Apply to equality/hash methods on value types
- Apply to frequently called math utilities
- Apply to forwarding methods (thin wrappers)
- Measure before and after

### Don't

- Don't apply to virtual methods (ignored)
- Don't apply to methods with try-catch blocks
- Don't apply to large methods (code bloat)
- Don't apply to cold paths (no benefit)
- Don't assume it always helps (measure!)

### Combining with readonly struct

```csharp
// Best combo: readonly struct + AggressiveInlining
public readonly struct Point
{
    public readonly float x;
    public readonly float y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float DistanceSquared(Point other)
    {
        float dx = x - other.x;
        float dy = y - other.y;
        return dx * dx + dy * dy;
    }
}
```

## Related Patterns

- [Readonly Struct with Cached Hash](../../value-equality-and-hashing/references/readonly-struct-cached-hash.md) - Uses AggressiveInlining
- [AggressiveInlining Performance Notes](./aggressive-inlining-performance-notes.md) - Benchmarks and JIT behavior

## See Also

- [AggressiveInlining for Hot Path Optimization](./aggressive-inlining.md)
