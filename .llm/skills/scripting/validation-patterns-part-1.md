---
title: "Validation Patterns and Duplicate Warning Prevention Part 1"
id: "validation-patterns-part-1"
category: "scripting"
version: "1.2.0"
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

Continuation material extracted from `validation-patterns.md` to keep .llm files within the 300-line budget.

## Solution

## Type Coercion Considerations

### When to Use `String()`

YAML parsers may return non-string types for values that look like numbers or dates:

```yaml
version: 1.0.0 # Parsed as number 1 (decimal truncated!)
created: 2026-01-30 # Parsed as Date object in some parsers
```

Use `String()` to safely coerce before string operations:

```javascript
// RIGHT: Coerce before regex matching
if (frontmatter.version != null && frontmatter.version !== "") {
  const versionStr = String(frontmatter.version);
  if (!versionStr.match(/^\d+\.\d+\.\d+$/)) {
    warnings.push(`Version '${versionStr}' should be semver format`);
  }
}

// WRONG: Assumes string type
if (frontmatter.version != null && frontmatter.version !== "") {
  if (!frontmatter.version.match(/^\d+\.\d+\.\d+$/)) {
    // TypeError if version is a number!
    warnings.push(`Invalid version format`);
  }
}
```

### Edge Cases with Type Coercion

| Input Value | `String(value)` Result | Notes                         |
| ----------- | ---------------------- | ----------------------------- |
| `1.0`       | `"1"`                  | Decimal .0 is lost!           |
| `null`      | `"null"`               | Check before coercing         |
| `undefined` | `"undefined"`          | Check before coercing         |
| `[1, 2]`    | `"1,2"`                | Array becomes comma-separated |
| `{a: 1}`    | `"[object Object]"`    | Objects need special handling |
| `true`      | `"true"`               | Boolean to string             |
| `0`         | `"0"`                  | Zero is preserved             |

**Best Practice**: Always check for `null`/empty before coercing:

```javascript
if (value != null && value !== "") {
  const strValue = String(value);
  // Now safe to validate strValue
}
```

## The Duplicate Warning Problem

### Anti-Pattern: Overlapping Validation Checks

```javascript
// BAD: Two separate if statements can both trigger for the same issue
function validateField(value, fieldName) {
  const warnings = [];

  // Check 1: Is the value invalid according to enum?
  if (!VALID_VALUES.includes(value)) {
    warnings.push(`Invalid ${fieldName}: '${value}'`);
  }

  // Check 2: Is the value missing or empty?
  if (value == null || value === "") {
    warnings.push(`Missing ${fieldName}`);
  }

  return warnings;
}

// When called with empty string:
validateField("", "complexity.level");
// Returns TWO warnings:
// - "Invalid complexity.level: ''"  (because '' is not in VALID_VALUES)
// - "Missing complexity.level"       (because '' === "")
```

### Correct Pattern: Mutually Exclusive Checks

```javascript
// GOOD: Ordered checks ensure exactly one warning
function validateField(value, fieldName) {
  const warnings = [];

  // Check 1: Is the value missing?
  if (value == null) {
    warnings.push(`Missing ${fieldName}`);
  }
  // Check 2: Is the value empty string?
  else if (value === "") {
    warnings.push(`Empty ${fieldName}`);
  }
  // Check 3: Is the value invalid? (Only check if value is present and non-empty)
  else if (!VALID_VALUES.includes(value)) {
    warnings.push(`Invalid ${fieldName}: '${value}'`);
  }

  return warnings;
}

// When called with empty string:
validateField("", "complexity.level");
// Returns exactly ONE warning:
// - "Empty complexity.level"
```

## Real-World Example: Skill File Validation

This pattern is implemented in [validate-skills.js](../../../scripts/validate-skills.js):

```javascript
// Validate complexity level - exclude empty values from enum check
if (
  frontmatter.complexity != null &&
  frontmatter.complexity.level != null &&
  frontmatter.complexity.level !== "" && // <-- Key: exclude empty strings
  !VALID_COMPLEXITY_LEVELS.includes(frontmatter.complexity.level)
) {
  warnings.push(
    new ValidationError(
      skillFile.relativePath,
      "complexity.level",
      `Invalid complexity level '${frontmatter.complexity.level}'. Valid: ${VALID_COMPLEXITY_LEVELS.join(", ")}`
    )
  );
}

// Later, check for missing/empty (mutually exclusive with above)
if (frontmatter.complexity == null || frontmatter.complexity.level == null) {
  warnings.push(
    new ValidationError(
      skillFile.relativePath,
      "complexity.level",
      `Missing 'complexity.level' - will show '?' in Complexity column of skills index`
    )
  );
} else if (frontmatter.complexity.level === "") {
  warnings.push(
    new ValidationError(
      skillFile.relativePath,
      "complexity.level",
      `Empty 'complexity.level' - will show '?' in Complexity column of skills index`
    )
  );
}
```

## Integration Testing for Warning Counts

Always write integration tests that verify the exact number of warnings produced:

```javascript
// GOOD: Integration test that verifies no duplicate warnings
describe("empty string complexity.level produces exactly one warning", () => {
  test("should produce exactly 1 warning (no duplicate from invalid enum check)", () => {
    const content = createValidFrontmatter({
      complexity: { level: "" }
    });
    const skillFile = createMockSkillFile(tempDir, content);

    const result = validateSkill(skillFile);

    // Filter warnings for the specific field
    const complexityWarnings = result.warnings.filter((w) => w.field === "complexity.level");

    // Key assertion: exactly ONE warning, not two
    expect(complexityWarnings).toHaveLength(1);
    expect(complexityWarnings[0].message).toContain("Empty 'complexity.level'");
  });
});
```

## See Also

- [Validation Patterns and Duplicate Warning Prevention](./validation-patterns.md)
