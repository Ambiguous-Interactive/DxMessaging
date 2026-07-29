# AggressiveInlining for Hot Path Optimization

> **One-line summary**: Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to hint the JIT compiler to inline small, hot methods, eliminating call overhead.

## Overview

Method calls have overhead: push arguments, call, pop return value. For very small methods called millions of times (e.g., property getters, math operations), this overhead can be significant. `AggressiveInlining` tells the JIT to strongly prefer inlining, replacing the call with the method body directly.

## Problem Statement

```csharp
// Without inlining hint, JIT may not inline this
public int GetValue()
{
    return _value;
}

// In a hot loop, call overhead accumulates
for (int i = 0; i < 1000000; i++)
{
    sum += obj.GetValue(); // Potential call overhead each iteration
}
```

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [aggressive inlining part 1](./aggressive-inlining-part-1.md)
- [aggressive inlining part 2](./aggressive-inlining-part-2.md)
