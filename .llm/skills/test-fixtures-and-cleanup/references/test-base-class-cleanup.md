# Test Base Class with Automatic Resource Cleanup

> **One-line summary**: Create an abstract test base class that automatically tracks and destroys GameObjects, Components, and other resources created during tests.

## Overview

Unity tests often create GameObjects and components that must be cleaned up to avoid test pollution. This pattern provides:

1. **Automatic tracking** via `Track<T>()` method
1. **Automatic destruction** in TearDown
1. **Scene cleanup** for integration tests
1. **Disposable tracking** for non-Unity resources

## Problem Statement

```csharp
// BAD: Manual cleanup is error-prone
[Test]
public void TestSomething()
{
    GameObject go = new GameObject("Test");
    var component = go.AddComponent<MyComponent>();

    // Test code...

    Object.DestroyImmediate(go); // Easy to forget!
}

// BAD: Cleanup in finally block is verbose
[Test]
public void TestSomething()
{
    GameObject go = null;
    try
    {
        go = new GameObject("Test");
        // Test...
    }
    finally
    {
        if (go != null) Object.DestroyImmediate(go);
    }
}
```

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [test base class cleanup part 1](./test-base-class-cleanup-part-1.md)
