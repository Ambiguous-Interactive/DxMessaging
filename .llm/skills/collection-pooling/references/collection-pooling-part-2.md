## Overview

Continuation material extracted from `collection-pooling.md` to keep .llm files within the 300-line budget.

## Solution

## Performance Notes

- **Allocations**: Zero in steady state; one-time pool warm-up
- **Timing**: O(1) rent/return; pool operations are constant time
- **Memory**: Pool grows to high-water mark, then stabilizes
- **Thread Safety**: Consider per-thread pools for multithreaded code

## Best Practices

### Do

- Use `using var` for concise syntax (C# 8+)
- Pre-size collections if you know approximate size: `list.Capacity = expectedCount`
- Pool collections used in Update/FixedUpdate/LateUpdate
- Clear collections before returning (handled automatically)

### Don't

- Don't store references to pooled collections beyond the using scope
- Don't return collections manually if using PooledResource
- Don't pool collections with finalizers or native resources
- Don't share PooledResource across threads

## Related Patterns

- [Object Pooling](../../object-pooling/references/object-pooling.md) - Base pooling pattern
- [StringBuilder Pooling](./stringbuilder-pooling.md) - Specialized string building
- [Array Pooling](./array-pooling.md) - Fixed-size buffer pooling

## See Also

- [Collection Pooling with RAII Pattern](./collection-pooling.md)
