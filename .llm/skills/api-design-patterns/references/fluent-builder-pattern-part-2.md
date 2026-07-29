## Overview

Continuation material extracted from `fluent-builder-pattern.md` to keep .llm files within the 300-line budget.

## Solution

## Best Practices

### Do

- Use struct for builders with few fields (<10)
- Return `this` by value (`var copy = this; ... return copy;`)
- Validate early in With\* methods where possible
- Validate configuration combinations in Build()
- Provide sensible defaults

### Don't

- Don't use struct for large builders (>200 bytes)
- Don't store reference types that might be mutated after Build()
- Don't make builders mutable (always return new copy)
- Don't forget the `initialized` flag pattern for distinguishing "not set" from "set to default"

### Initialization Pattern

```csharp
public struct Builder
{
    private int value;
    private bool valueSet; // Distinguishes "0" from "not set"

    public Builder WithValue(int v)
    {
        var copy = this;
        copy.value = v;
        copy.valueSet = true;
        return copy;
    }

    public Result Build()
    {
        int finalValue = valueSet ? value : 100; // Default is 100
        return new Result(finalValue);
    }
}
```

## Related Patterns

- [Collection Extensions](./collection-extensions.md) - Fluent collection API
- [Try-Pattern APIs](./try-pattern-apis.md) - Safe configuration validation
- [Fluent Builder Templates and Factories](./fluent-builder-pattern-templates.md) - Reusable patterns
- [Fluent Builder Usage Examples](./fluent-builder-pattern-usage-examples.md) - Common flows

## See Also

- [Fluent Builder Pattern with Struct Builders](./fluent-builder-pattern.md)
