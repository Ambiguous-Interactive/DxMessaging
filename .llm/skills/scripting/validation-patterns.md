---
title: "Validation Patterns and Duplicate Warning Prevention"
id: "validation-patterns"
category: "scripting"
version: "1.3.0"
created: "2026-01-30"
updated: "2026-03-17"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/validate-skills.js"
    - path: "scripts/__tests__/validate-skills-optional-fields.test.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "validation"
  - "javascript"
  - "error-handling"
  - "duplicate-warnings"
  - "enum-validation"
  - "optional-fields"
  - "testing"
  - "truthiness"
  - "type-coercion"
  - "quote-validation"
  - "filesystem"
  - "git"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of validation flow, conditional logic ordering, and JavaScript type coercion"

impact:
  performance:
    rating: "none"
    details: "Validation patterns only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Prevents confusing duplicate warnings that obscure actual issues"
  testability:
    rating: "high"
    details: "Clear validation logic is easier to test exhaustively"

prerequisites:
  - "JavaScript fundamentals"
  - "Understanding of null/undefined distinction"
  - "Basic validation concepts"

dependencies:
  skills:
    - "javascript-code-quality"

applies_to:
  languages:
    - "JavaScript"
    - "TypeScript"
  frameworks:
    - "Node.js"

aliases:
  - "duplicate warning prevention"
  - "validation deduplication"
  - "enum validation"
  - "presence vs truthiness"

related:
  - "javascript-code-quality"
  - "comprehensive-test-coverage"

status: "stable"
---

# Validation Patterns and Duplicate Warning Prevention

> **One-line summary**: Structure validation logic to produce exactly one warning per issue,
> not duplicates from multiple overlapping checks.

## Overview

When validating fields that have multiple possible error conditions (missing, empty, invalid enum
value), careless ordering can produce duplicate warnings for the same underlying issue. This
confuses users and makes debugging harder.

## Solution

1. **Order validation checks from most specific to most general** - check for missing/empty before
   checking for invalid values
1. **Use else-if chains** to ensure only one warning per field
1. **Exclude empty/null values from enum validation** - an empty string is "empty," not "invalid enum"
1. **Require matching quote boundaries before unquoting** - strip quotes only when both sides are present and the same quote character
1. **Write integration tests** that verify exactly one warning per field condition
1. **Include malformed quote tests** - cover mismatched and unclosed quotes so parsers do not silently normalize invalid input
1. **Use explicit presence checks** - avoid truthiness-based validation that conflates different issues
1. **Never silently swallow `readdirSync` failures** - catch and log warnings for skipped directories
1. **Gate repository policy checks to tracked files** - skip untracked gitignored local files in pre-commit/pre-push validators

## Filesystem and Hook Safety

When scripts recursively scan directories, silent catch-and-return patterns can hide partial scans and
create false success messages. Always surface directory read failures to users.

Use this pattern:

```javascript
function walk(dir, files = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    console.warn(`Warning: Unable to read directory ${dir}: ${error.message}`);
    return files;
  }

  for (const entry of entries) {
    // ... recursion and filtering
  }

  return files;
}
```

For pre-commit/pre-push validators, do not validate local untracked files that are intentionally
gitignored. Confirm the target path is tracked first:

```javascript
const result = childProcess.spawnSync("git", [
  "ls-files",
  "--error-unmatch",
  "--",
  ".vscode/settings.json"
]);

if (result.status !== 0) {
  console.log("Found local settings file, but it is not tracked by git; skipping validation.");
  return;
}
```

This prevents unrelated commits from failing because of personal local workspace settings.

## Quote-Boundary Validation

When parsing YAML/TOML scalar values, avoid regex patterns that remove leading or trailing quotes independently.
For example, `/^["']|["']$/g` can transform malformed input into apparently valid values.

Use a boundary check first:

```javascript
function stripMatchingBoundaryQuotes(value) {
  const trimmed = value.trim();
  if (trimmed.length < 2) {
    return trimmed;
  }

  const first = trimmed[0];
  const last = trimmed[trimmed.length - 1];
  const hasMatchingQuotes = (first === '"' || first === "'") && first === last;

  return hasMatchingQuotes ? trimmed.slice(1, -1) : trimmed;
}
```

Test at least these malformed inputs:

- `"value'` (mismatched boundaries)
- `'value"` (mismatched boundaries)
- `"value` (missing closing quote)
- `value"` (missing opening quote)

## Truthiness vs Presence Checks

### The Anti-Pattern: Using Truthiness for Presence

Truthiness-based checks (`!value`, `if (value)`) conflate multiple distinct conditions, producing
misleading error messages and duplicate warnings.

```javascript
// WRONG: Truthiness-based validation
function validateField(frontmatter, field) {
  if (!frontmatter[field]) {
    // This triggers for:
    // - undefined (missing)
    // - null (missing)
    // - "" (empty string - should be separate error)
    // - 0 (legitimate value for numeric fields!)
    // - false (legitimate value for boolean fields!)
    errors.push(`Required field '${field}' is missing`);
  }
}
```

### The Correct Pattern: Explicit Presence Checks

Use explicit null/undefined checks, then separate empty string checks:

```javascript
// RIGHT: Presence-based validation with explicit checks
function validateField(frontmatter, field) {
  if (frontmatter[field] === undefined || frontmatter[field] === null) {
    errors.push(`Required field '${field}' is missing`);
  } else if (frontmatter[field] === "") {
    errors.push(`Required field '${field}' is empty`);
  }
}

// ALSO RIGHT: Using loose equality shorthand (null == undefined is true)
function validateField(frontmatter, field) {
  if (frontmatter[field] == null) {
    errors.push(`Required field '${field}' is missing`);
  } else if (frontmatter[field] === "") {
    errors.push(`Required field '${field}' is empty`);
  }
}
```

### The Guard Clause Pattern: `!= null && !== ''`

When validating a value (checking if it's in an enum, matches a regex, etc.), guard against
both missing and empty values to prevent spurious "invalid value" errors:

```javascript
// RIGHT: Guard against missing and empty before validating
if (value != null && value !== "" && !VALID_VALUES.includes(value)) {
  errors.push(`Invalid ${fieldName}: '${value}'`);
}

// WRONG: Missing guard allows empty string to be flagged as "invalid"
if (!VALID_VALUES.includes(value)) {
  // Empty string "" will produce: "Invalid fieldName: ''"
  // Should be "fieldName is empty" instead!
  errors.push(`Invalid ${fieldName}: '${value}'`);
}
```

## See Also

- [validation patterns part 1](./validation-patterns-part-1.md)
- [validation patterns part 2](./validation-patterns-part-2.md)
