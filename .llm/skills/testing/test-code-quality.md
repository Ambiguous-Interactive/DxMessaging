---
id: test-code-quality
title: Test Code Quality and Accuracy
description: Best practices for test documentation accuracy, linter directive placement, and user-facing message consistency
category: testing
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"
status: stable

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/__tests__/validate-skills-tags.test.js"
    - path: "scripts/__tests__/validate-skills-optional-fields.test.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - testing
  - documentation
  - linting
  - code-quality
  - javascript
related:
  - comprehensive-test-coverage
  - script-test-coverage
complexity:
  level: intermediate
  reasoning: "Requires understanding of JavaScript truthiness and test organization principles"
impact:
  performance:
    rating: medium
    description: Prevents misleading documentation and ensures test accuracy
  maintainability:
    rating: high
    description: "Accurate test documentation improves codebase understanding"
---

# Test Code Quality and Accuracy

## Overview

This skill covers best practices for writing accurate test documentation, proper linter directive placement, and ensuring consistency between user-facing messages and actual UI/output.

## Solution

## Linter Directive Placement

### The Problem

ESLint disable directives must be placed correctly to work:

```javascript
// WRONG: Directive is NOT immediately before the line it suppresses
// eslint-disable-next-line unicorn/no-new-array
const frontmatter = {
  title: "Sample",
  tags: new Array("testing") // This line is NOT suppressed!
};

// CORRECT: Directive is immediately before the target line
const frontmatter = {
  title: "Sample",
  // eslint-disable-next-line unicorn/no-new-array
  tags: new Array("testing") // This line IS suppressed
};
```

### Best Practices

1. **`eslint-disable-next-line` suppresses exactly the next line** - not code several lines later
1. **Verify the linter is actually configured** - don't add directives for linters that aren't used
1. **Check `package.json` and config files** - look for `.eslintrc*`, `eslint.config.*`, or `eslint` in `devDependencies`
1. **Remove unnecessary directives** - if no linter is configured, the directive is dead code

## Test Documentation Accuracy

### The Problem

Test describe blocks and file headers must accurately describe what tests they contain.

```javascript
// WRONG: Header says "falsy/undefined" but tests wrong-type values
/**
 * These tests validate:
 * - Missing tags (falsy/undefined)  // Inaccurate!
 */

describe("missing tags field", () => {
  test("should warn when tags is empty string", () => {
    // WRONG PLACE
    // Empty string is NOT "missing" - it's a wrong type
  });
});

// CORRECT: Accurate categorization
/**
 * These tests validate:
 * - Missing tags (undefined/null)
 * - Wrong type for tags (string, number, boolean)
 */

describe("missing tags field", () => {
  test("should warn when tags is undefined", () => {
    /* ... */
  });
  test("should warn when tags is null", () => {
    /* ... */
  });
});

describe("wrong type for tags", () => {
  test("should warn when tags is an empty string", () => {
    /* ... */
  });
  test("should warn when tags is a zero", () => {
    /* ... */
  });
  test("should warn when tags is boolean false", () => {
    /* ... */
  });
});
```

### JavaScript Truthiness Reference

Understanding JavaScript's truthy/falsy values prevents misclassification:

| Value               | Truthy/Falsy | Category             | Correct Test Location    |
| ------------------- | ------------ | -------------------- | ------------------------ |
| `undefined`         | Falsy        | Missing              | "missing field" describe |
| `null`              | Falsy        | Missing              | "missing field" describe |
| `""` (empty string) | Falsy        | Wrong type           | "wrong type" describe    |
| `0`                 | Falsy        | Wrong type           | "wrong type" describe    |
| `false`             | Falsy        | Wrong type           | "wrong type" describe    |
| `[]` (empty array)  | **Truthy**   | Correct type (empty) | "empty array" describe   |
| `{}` (empty object) | **Truthy**   | Wrong type           | "wrong type" describe    |

### Key Distinction

- **Missing**: Value is `undefined` or `null` - the field doesn't exist or is explicitly null
- **Wrong type**: Value exists but is the wrong JavaScript type (string instead of array, etc.)
- **Empty**: Value exists with correct type but has no content (empty array, empty string)

## User-Facing Message Consistency

### The Problem

Warning messages and error messages displayed to users must match actual UI/output terminology.

```javascript
// WRONG: Message says "Difficulty column" but actual table header is "Complexity"
warnings.push(`Missing 'complexity.level' - will show '?' in Difficulty column`);

// CORRECT: Message matches actual column name
warnings.push(`Missing 'complexity.level' - will show '?' in Complexity column`);
```

### Best Practices

1. **Verify against source of truth** - read the actual output generation code to confirm terminology
1. **Use grep/search to find actual column names** - e.g., search for table header generation
1. **Keep messages synchronized** - when UI changes, update all related messages
1. **Add SYNC notes** - reference the source of truth in comments

Example:

```javascript
// SYNC: Column names must match table headers in generate-skills-index.js
// Actual headers: | Skill | Lines | Complexity | Status | Performance | Tags |
`Missing 'complexity.level' - will show '?' in Complexity column of skills index`;
```

## Test Coverage for Message Content

### The Problem

When user-facing messages are changed, tests should verify the message content is correct.

### Best Practice

Add tests that check message content matches actual terminology:

```javascript
describe("warning message content", () => {
  test("should reference correct Complexity column name", () => {
    const warnings = validateComplexityLevel(frontmatter, testPath);

    expect(warnings[0].message).toContain("Complexity column");
    // Ensures we don't accidentally say "Difficulty column"
  });

  test("should reference correct Performance column name", () => {
    const warnings = validatePerformanceRating(frontmatter, testPath);

    expect(warnings[0].message).toContain("Performance column");
    // Ensures we don't accidentally say "Priority column"
  });
});
```

## Bidirectional SYNC Notes

### The Problem

SYNC notes only work when they exist in both directions.

```javascript
// FILE A: validate-skills.js
// SYNC: Keep logic in sync with validate-skills-tags.test.js validateTags()
if (frontmatter.tags === undefined || frontmatter.tags === null) {

// FILE B: validate-skills-tags.test.js
/**
 * SYNC: Keep logic in sync with validate-skills.js validateSkill() tags validation block
 */
function validateTags(frontmatter, relativePath) {
```

### Requirements

1. **Always bidirectional** - when A references B, B must reference A
1. **Reference function/block names** - not line numbers (which become stale)
1. **Be specific** - identify the exact function or logical block
1. **Verify references exist** - confirm the target exists before adding a SYNC note

## Pre-Commit Checklist

Before committing test code, verify:

- [ ] Linter directives are immediately before the line they suppress
- [ ] Linter is actually configured (check `package.json`, `.eslintrc*`, etc.)
- [ ] `describe()` block names accurately reflect the tests they contain
- [ ] File header comments accurately describe test categories
- [ ] "Missing" vs "wrong type" vs "empty" tests are correctly categorized
- [ ] User-facing messages match actual UI/output terminology
- [ ] Message content is tested if it references specific column names or labels
- [ ] SYNC notes exist in both directions

## See Also

- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Script Test Coverage](script-test-coverage.md)
