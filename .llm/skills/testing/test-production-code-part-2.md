---
title: "Test Production Code Directly Part 2"
id: "test-production-code-part-2"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
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

Continuation material extracted from `test-production-code.md` to keep .llm files within the 300-line budget.

## Solution

## SYNC Note Best Practices

When duplication is unavoidable, follow these SYNC note guidelines:

1. **Always bidirectional**: Both files must reference each other
1. **Never use line numbers**: Reference function names, not line numbers that change
1. **Use descriptive identifiers**: `SYNC: Keep in sync with validate.js isValidEmail()` not `SYNC: Keep in sync with validate.js line 42`
1. **Verify references exist**: Confirm the referenced function exists before adding the note
1. **Update both on changes**: When modifying synced code, update both locations

## Red Flags to Watch For

These patterns suggest tests may not be exercising production code:

| Red Flag                                               | Why It's Suspicious           |
| ------------------------------------------------------ | ----------------------------- |
| Test file defines validation constants                 | Should import from production |
| Test file has utility functions that mirror production | Should import instead         |
| Test never imports the module it's supposedly testing  | Tests itself, not production  |
| Test file size rivals production file size             | Too much duplicated logic     |
| Same bug exists in production and tests                | Copied code with copied bugs  |

## Verification Checklist

Before merging, verify tests exercise production code:

1. **Check imports**: Does the test file import from the production module?
1. **Check coverage**: Does production file show coverage when tests run?
1. **Mutation test**: Change production code slightly - do tests fail?
1. **Review test functions**: Are they calling production code or local copies?
1. **Search for duplication**: Do test and production files have similar function bodies?

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md) - What to test
- [Test Code Quality skill](test-code-quality.md) - Test documentation accuracy

## Related Links

- [Test Production Code Directly](./test-production-code.md)
