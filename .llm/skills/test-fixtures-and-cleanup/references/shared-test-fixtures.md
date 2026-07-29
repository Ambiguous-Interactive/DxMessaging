# Shared Test Fixtures with Reference Counting

> **One-line summary**: Share expensive test fixtures across test classes using thread-safe reference counting.

## Overview

Some test fixtures are expensive to create (large textures, asset bundles, scenes). Creating them per test wastes time and memory. Shared fixtures allow multiple test classes to reuse a single instance and clean up when the last user releases it.

## Problem Statement

```csharp
[TestFixture]
public class TextureTestsA
{
    private Texture2D largeTexture;

    [OneTimeSetUp]
    public void Setup()
    {
        largeTexture = new Texture2D(4096, 4096); // 64MB, 500ms
    }
}

[TestFixture]
public class TextureTestsB
{
    private Texture2D largeTexture; // Another 64MB, another 500ms
}
```

## Solution

### Core Concept

```text
AcquireFixtures() -> refCount++ -> create on first acquire
ReleaseFixtures() -> refCount-- -> destroy on last release
```

Use a shared static fixture with a reference count and explicit acquire/release calls from each test fixture.

## Usage

```csharp
[TestFixture]
[Category("Slow")]
public sealed class TextureProcessingTestsA : CommonTestBase
{
    [OneTimeSetUp]
    public override void CommonOneTimeSetUp()
    {
        base.CommonOneTimeSetUp();
        SharedTextureTestFixtures.AcquireFixtures();
    }

    [OneTimeTearDown]
    public override IEnumerator CommonOneTimeTearDown()
    {
        SharedTextureTestFixtures.ReleaseFixtures();
        yield return base.CommonOneTimeTearDown();
    }
}
```

## Performance Notes

- **Savings**: N test classes x fixture time -> 1 x fixture time
- **Memory**: Peak usage reduced to a single shared instance
- **Thread Safety**: Lock overhead is negligible

## Best Practices

### Do

- Use shared fixtures only for expensive resources
- Pair Acquire/Release in OneTimeSetUp/OneTimeTearDown
- Mark tests using shared fixtures with `[Category("Slow")]`
- Treat shared fixtures as immutable

### Don't

- Don't access fixtures without acquiring first
- Don't forget to release (memory leak)
- Don't mutate shared fixtures between tests

## See Also

- [Shared Fixtures: Reference Counting](./shared-test-fixtures-reference-counting.md)
- [Shared Fixtures: Generic Base](./shared-test-fixtures-generic-base.md)
- [Test Base Class Cleanup](./test-base-class-cleanup.md)
- [Test Categories](../../unity-test-execution/references/test-categories.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-21 | Initial version |
