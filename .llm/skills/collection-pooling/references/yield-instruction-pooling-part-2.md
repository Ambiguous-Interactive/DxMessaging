## Overview

Continuation material extracted from `yield-instruction-pooling.md` to keep .llm files within the 300-line budget.

## Solution

## Usage

### Basic Coroutine

```csharp
private IEnumerator SpawnEnemies()
{
    while (true)
    {
        SpawnEnemy();
        yield return Buffers.GetWaitForSeconds(2f); // Cached!
    }
}
```

### Common Yield Instructions

```csharp
private IEnumerator UpdateLoop()
{
    while (true)
    {
        // These are static singletons - always zero allocation
        yield return Buffers.WaitForEndOfFrame;
        UpdateUI();

        yield return Buffers.WaitForFixedUpdate;
        UpdatePhysics();

        // Cached by duration
        yield return Buffers.GetWaitForSeconds(0.5f);
    }
}
```

### Realtime Waits (Pause-Safe)

```csharp
private IEnumerator FadeOut()
{
    float elapsed = 0f;
    float duration = 1f;

    while (elapsed < duration)
    {
        elapsed += Time.unscaledDeltaTime;
        SetAlpha(1f - (elapsed / duration));
        yield return Buffers.GetWaitForSecondsRealtime(0.016f); // ~60fps
    }
}
```

### Memory-Constrained Fallback

```csharp
private IEnumerator DynamicDelay(float delay)
{
    WaitForSeconds wait = Buffers.TryGetWaitForSecondsPooled(delay, maxCacheSize: 50);

    if (wait != null)
    {
        yield return wait;
    }
    else
    {
        // Cache full, create temporary (will be GC'd)
        yield return new WaitForSeconds(delay);
    }
}
```

## Performance Notes

- **Allocations**: Zero after initial cache population
- **Cache Size**: ~20 bytes per cached duration
- **Quantization**: 0.05s step = max 200 entries for 0-10s range
- **Lookup**: O(1) dictionary access

### Cache Growth Behavior

| Duration Range | Quantization | Max Entries |
| -------------- | ------------ | ----------- |
| 0-1s           | 0.05s        | 20          |
| 0-5s           | 0.05s        | 100         |
| 0-10s          | 0.1s         | 100         |

## Best Practices

### Do

- Use static singleton properties for `WaitForEndOfFrame`/`WaitForFixedUpdate`
- Use `GetWaitForSeconds` for common durations
- Call `ClearYieldCaches()` on scene transitions if memory is tight
- Adjust `WaitInstructionQuantizationStepSeconds` based on precision needs

### Don't

- Don't create new `WaitForSeconds` in hot coroutines
- Don't cache unique random durations (defeats purpose)
- Don't use very small quantization steps (cache bloat)
- Don't share `WaitForSecondsRealtime` across threads (not thread-safe)

### Quantization Trade-offs

```csharp
// Precise (more cache entries)
Buffers.WaitInstructionQuantizationStepSeconds = 0.01f;

// Balanced (default)
Buffers.WaitInstructionQuantizationStepSeconds = 0.05f;

// Coarse (fewer entries, less precision)
Buffers.WaitInstructionQuantizationStepSeconds = 0.25f;
```

## Related Patterns

- [Object Pooling](../../object-pooling/references/object-pooling.md) - General pooling pattern
- [Collection Pooling](./collection-pooling.md) - Pooling with RAII

## See Also

- [WaitForSeconds and Yield Instruction Pooling](./yield-instruction-pooling.md)
