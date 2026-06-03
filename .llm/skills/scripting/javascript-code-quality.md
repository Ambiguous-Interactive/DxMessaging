---
title: "JavaScript Code Quality Practices"
id: "javascript-code-quality"
category: "scripting"
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/"
    - path: "scripts/__tests__/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "javascript"
  - "code-quality"
  - "linting"
  - "testing"
  - "jest"
  - "eslint"
  - "documentation"
  - "sync-notes"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of linting tools, test organization, and code documentation"

impact:
  performance:
    rating: "none"
    details: "Code quality patterns only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Proper code organization and documentation prevents confusion and bugs"
  testability:
    rating: "high"
    details: "Well-organized tests are easier to maintain and extend"

prerequisites:
  - "JavaScript fundamentals"
  - "Jest testing framework familiarity"
  - "Understanding of linting concepts"

dependencies:
  packages:
    - "jest"
  skills:
    - "script-test-coverage"
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "Jest"
    - "Node.js"
  versions:
    node: ">=18.0"
    jest: ">=29.0"

aliases:
  - "JS code quality"
  - "JavaScript best practices"
  - "Test organization"

related:
  - "script-test-coverage"
  - "cross-platform-compatibility"
  - "comprehensive-test-coverage"
  - "validation-patterns"

status: "stable"
---

# JavaScript Code Quality Practices

> **One-line summary**: Ensure JavaScript code has accurate documentation, properly placed
> directives, correctly categorized tests, and bidirectional synchronization notes.

## Overview

JavaScript code quality extends beyond just "working code." This skill covers common pitfalls in
documentation accuracy, linter directive placement, test organization, and synchronization between
related code. Following these practices prevents confusion, reduces bugs, and improves codebase
maintainability.

## Solution

1. **Keep JSDoc accurate** - function descriptions, parameter types, and return values must match
   implementation
1. **Keep module exports documented** - `@exports` tags must list actual exports with correct types
   and values
1. **Place linter directives immediately before suppressed code** - `eslint-disable-next-line`
   only affects the next line
1. **Only use linter directives if the linter is configured** - don't add ESLint comments to
   projects that don't use ESLint
1. **Use accurate `describe` block names** - test categories must reflect actual test content
1. **Match user-facing messages to actual UI terminology** - column names, labels, and terms must
   be consistent
1. **Add bidirectional SYNC notes** - when code A references code B, code B must reference code A
1. **Test user-facing message content** - verify messages contain correct terminology

## Linter Directive Placement

### Problem

Linter disable directives (ESLint, JSHint, etc.) must be placed immediately before the line they
suppress. Placing them earlier in the file has no effect and creates confusion.

### Anti-Pattern

```javascript
// BAD: Directive is too far from the code it's meant to suppress
// eslint-disable-next-line no-unused-vars
const SOME_CONSTANT = "value";

// Many lines of other code here...

function unusedFunction() {
  // This function is NOT suppressed - the directive was consumed by SOME_CONSTANT
}
```

### Correct Pattern

```javascript
// GOOD: Directive immediately precedes the suppressed code
const SOME_CONSTANT = "value";

// Many lines of other code here...

// eslint-disable-next-line no-unused-vars
function unusedFunction() {
  // This function IS suppressed
}
```

### When to Avoid Directives Entirely

Before adding a linter directive, verify:

1. **Is the linter actually configured?** Adding ESLint directives to a project that doesn't use
   ESLint is misleading and creates maintenance burden.
1. **Can the underlying issue be fixed?** Unused variables should usually be removed, not
   suppressed.
1. **Is the suppression documented?** If you must suppress, add a comment explaining why.

```javascript
// GOOD: If ESLint is not used in the project, don't add ESLint directives
// Just remove or use the code properly instead of suppressing warnings
```

## See Also

- [javascript code quality part 1](./javascript-code-quality-part-1.md)
- [javascript code quality part 2](./javascript-code-quality-part-2.md)
