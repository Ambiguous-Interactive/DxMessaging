---
title: "Test Production Code Directly"
id: "test-production-code"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/__tests__/"
    - path: "scripts/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "anti-patterns"
  - "code-quality"
  - "javascript"
  - "maintainability"
  - "testability"
  - "best-practices"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of test design principles and code architecture"

impact:
  performance:
    rating: "none"
    details: "Testing patterns only; no runtime performance impact"
  maintainability:
    rating: "critical"
    details: "Prevents tests from diverging from production behavior"
  testability:
    rating: "critical"
    details: "Ensures tests actually verify production code correctness"

prerequisites:
  - "Understanding of module exports and imports"
  - "Familiarity with test isolation principles"

dependencies:
  packages: []
  skills:
    - "comprehensive-test-coverage"
    - "script-test-coverage"

applies_to:
  languages:
    - "JavaScript"
    - "TypeScript"
    - "C#"
  frameworks:
    - "Jest"
    - "Node.js"
    - "NUnit"
  versions:
    node: ">=18.0"
    jest: ">=29.0"

aliases:
  - "Test real code"
  - "Don't re-implement in tests"
  - "Test production directly"
  - "Avoid test duplication"

related:
  - "comprehensive-test-coverage"
  - "script-test-coverage"
  - "test-code-quality"

status: "stable"
---

# Test Production Code Directly

> **One-line summary**: Tests must import and use production code, never re-implement production logic locally.

## Overview

A common anti-pattern in testing is re-implementing production validation logic inside test files. When tests maintain their own copies of validation rules, they can pass even when production code regresses. This skill documents how to structure code for testability and avoid this dangerous pattern.

## Problem Statement

### The Anti-Pattern

Tests that re-implement production logic create a false sense of security:

```javascript
// PRODUCTION: scripts/validate-data.js
function validateRecord(record) {
  const errors = [];
  if (!record.name || record.name.length < 3) {
    errors.push("Name must be at least 3 characters");
  }
  if (!record.email || !record.email.includes("@")) {
    errors.push("Email must be valid");
  }
  return errors;
}

// TEST: scripts/__tests__/validate-data.test.js
// WRONG: Re-implementing validation locally
function localValidateRecord(record) {
  const errors = [];
  if (!record.name || record.name.length < 3) {
    // Duplicated logic!
    errors.push("Name must be at least 3 characters");
  }
  if (!record.email || !record.email.includes("@")) {
    // Duplicated logic!
    errors.push("Email must be valid");
  }
  return errors;
}

test("should validate record correctly", () => {
  const result = localValidateRecord({ name: "Jo", email: "bad" });
  expect(result).toHaveLength(2); // Tests pass, but production untested!
});
```

### Why This Is Dangerous

1. **Production regressions go undetected**: If someone changes `< 3` to `< 2` in production, tests still pass because they use the local copy.
1. **Maintenance burden doubles**: Every production change requires updating test copies.
1. **Divergence over time**: Local test copies gradually drift from production reality.
1. **False confidence**: 100% test coverage means nothing if tests don't exercise production code.
1. **Bug duplication**: If the same bug exists in both copies, tests won't catch it.

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [test production code part 1](./test-production-code-part-1.md)
- [test production code part 2](./test-production-code-part-2.md)
