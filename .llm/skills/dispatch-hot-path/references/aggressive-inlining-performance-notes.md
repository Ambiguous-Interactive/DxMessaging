# AggressiveInlining Performance Notes

> **One-line summary**: Benchmark and JIT behavior notes for aggressive inlining decisions.

## Overview

This skill captures benchmark results and JIT behavior that inform inlining decisions.

## Solution

Use the notes below to decide when AggressiveInlining provides measurable wins.

## Performance Notes

### Benchmark: 10M Dictionary Lookups

| Approach                   | Time    |
| -------------------------- | ------- |
| Without AggressiveInlining | 142ms   |
| With AggressiveInlining    | 98ms    |
| **Improvement**            | **31%** |

### JIT Behavior

- **Default**: JIT inlines methods < 32 IL bytes
- **AggressiveInlining**: JIT tries harder, may inline larger methods
- **Not a guarantee**: JIT can still refuse (virtual, try-catch, etc.)
