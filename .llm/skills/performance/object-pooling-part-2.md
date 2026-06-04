---
title: "Object Pooling for Zero-Allocation Messaging Part 2"
id: "object-pooling-part-2"
category: "performance"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-03-16"
status: "stable"
tags:
  - migration
  - split
complexity:
  level: "intermediate"
impact:
  performance:
    rating: "low"
---

## Overview

Continuation material extracted from `object-pooling.md` to keep .llm files within the 300-line budget.

## Solution

## Performance Notes

**Benchmarks** (10,000 messages/frame, Unity 2021.3, IL2CPP):

| Approach        | Allocations/Frame | GC Pressure | Frame Time |
| --------------- | ----------------- | ----------- | ---------- |
| `new` each time | 10,000            | 400 KB      | 2.1 ms     |
| Object pool     | 0\*               | 0 KB        | 0.8 ms     |
| Struct messages | 0                 | 0 KB        | 0.5 ms     |

\* After warm-up; initial frames allocate to fill pool

**When pooling matters**:

- High-frequency events (>100/second)
- Mobile/console targets with memory constraints
- Games targeting consistent 60+ FPS

**When pooling is overkill**:

- Editor tools
- One-shot events (level start, game over)
- Low-frequency UI events

## See Also

- [Object Pooling Variations](./object-pooling-variations.md)
- [Object Pooling Usage Examples](./object-pooling-usage-examples.md)
- [Object Pooling Anti-Patterns](./object-pooling-anti-patterns.md)

## References

- [Unity Performance Best Practices](https://docs.unity3d.com/Manual/BestPracticeUnderstandingPerformanceInUnity.html)
- [.NET Object Pooling](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.objectpool)
- [Game Programming Patterns: Object Pool](https://gameprogrammingpatterns.com/object-pool.html)

## Changelog

| Version | Date       | Changes                                          |
| ------- | ---------- | ------------------------------------------------ |
| 1.0.0   | 2026-01-21 | Initial version with core pattern and variations |

## Related Links

- [Object Pooling for Zero-Allocation Messaging](./object-pooling.md)
