---
title: "Git and Parser Robustness in CI/CD Part 1"
id: "git-workflow-robustness-part-1"
category: "testing"
version: "1.0.0"
created: "2026-01-27"
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

Continuation material extracted from `git-workflow-robustness.md` to keep .llm files within the 300-line budget.

## Solution

## Markdown Inline Code Parsing

### CommonMark Multi-Backtick Semantics

CommonMark defines specific rules for code spans with multiple backticks:

1. Opening and closing sequences must have the **same number of backticks**
1. Content can contain backticks with fewer characters than the delimiter
1. A single backtick inside double-backtick delimiters is literal text

#### Examples

````markdown
`code` -> <code>code</code>
`code` -> <code>code</code>
`` `code` `` -> <code>`code`</code>
```a`b`` -> <code>a`b</code> (opened with ``, closed with ``)
`` a ` b ``      to  <code>a` b</code>
````

### Parsing Algorithm

A correct inline code parser must:

1. Detect opening backtick sequences and count their length
1. Search for a closing sequence of **exactly the same length**
1. Handle nested or unmatched backticks as literal content
1. Strip one leading and one trailing space when both are present

**Key implementation requirements:**

- Count opening backticks and search for closing sequence of **exactly the same length**
- Verify the closing sequence isn't part of a longer backtick run
- Strip one leading and trailing space when both are present (CommonMark rule)
- Treat unmatched opening sequences as literal text

### Common Parsing Pitfalls

1. **Greedy matching**: Using regex like `` `[^`]+` `` fails on nested backticks
1. **Ignoring backtick count**: Treating all backtick sequences as equivalent
1. **Missing space stripping**: Not handling `` ` code ` `` correctly
1. **Partial matches**: Matching ` `` ` inside ` ``` ` sequences

## Test Coverage for Parsers

### Edge Case Categories

When testing parsers, cover these categories:

| Category      | Examples                                       |
| ------------- | ---------------------------------------------- |
| Empty/minimal | `''`, `` ` ``, ` `` `                          |
| Boundary      | Code at start/end, only code, adjacent spans   |
| Nested        | ``` `` `nested` `` ```, unequal counts         |
| Whitespace    | Leading/trailing spaces, only spaces, newlines |
| Unicode       | accented chars (U+00E9), emoji, zero-width     |

### Data-Driven Test Patterns

Use parameterized tests for comprehensive coverage:

```javascript
describe("inline code parsing", () => {
  const testCases = [
    // [input, expectedOutput, description]
    ["`code`", [{ type: "code", content: "code" }], "simple code span"],
    ["``a`b``", [{ type: "code", content: "a`b" }], "backtick in code"],
    ["` a `", [{ type: "code", content: "a" }], "space stripping"],
    [
      "`a``b`",
      [
        { type: "code", content: "a" },
        { type: "code", content: "b" }
      ],
      "adjacent spans"
    ]
  ];

  test.each(testCases)("%s  to  %j (%s)", (input, expected, _desc) => {
    expect(parseInlineCode(input)).toEqual(expected);
  });
});
```

### Property-Based Testing

For parser robustness, use property-based tests with libraries like `fast-check`:

- **Never throws**: Parser should handle any input without throwing
- **Content preservation**: Total output length <= input length (accounting for delimiters)
- **Idempotence**: Re-parsing output yields same structure

## CI/CD Integration Patterns

### Fail-Safe Git Operations

Structure git operations in CI to handle all edge cases:

```yaml
# GitHub Actions example
- name: Check for changes
  id: changes
  run: |
    # Handle initial commit
    if git rev-parse HEAD~1 >/dev/null 2>&1; then
      CHANGED_FILES=$(git diff --name-only HEAD~1 HEAD)
    else
      # First commit - all files are "new"
      CHANGED_FILES=$(git ls-tree --name-only -r HEAD)
    fi
    echo "files=$CHANGED_FILES" >> $GITHUB_OUTPUT
```

### Parser Testing in CI

Ensure parser tests run with comprehensive edge cases:

```yaml
- name: Run parser tests
  run: |
    npm test -- --coverage --coverageThreshold='{"global":{"branches":90}}'
```

### Handling `grep` Exit Codes

`grep` returns exit code 1 when no matches are found, which fails CI pipelines:

```bash
# DANGEROUS: Fails CI when no matches
grep "pattern" file.txt

# SAFE: Handle no-match case
grep "pattern" file.txt || true

# SAFE: Use count with fallback
COUNT=$(grep -c "pattern" file.txt 2>/dev/null || echo "0")
```

## Validation Checklist

Before merging code with git commands or parsers:

- [ ] Git commands handle initial/empty commits
- [ ] Git commands work with shallow clones
- [ ] Git commands handle detached HEAD states
- [ ] File paths are properly quoted
- [ ] Parsers handle empty input
- [ ] Parsers handle malformed/unmatched delimiters
- [ ] Parsers handle unicode and special characters
- [ ] Tests cover boundary conditions
- [ ] Tests use data-driven patterns for edge cases
- [ ] CI pipelines handle `grep` exit codes properly

## See Also

- [Test Coverage Requirements](../testing/comprehensive-test-coverage.md) - Detailed testing
  strategies
- [Documentation Updates](../documentation/documentation-updates.md) - Keeping docs in sync
- [Script and Automation Conventions](../../context.md#script-and-automation-conventions) - Main context file patterns

## Related Links

- [Git and Parser Robustness in CI/CD](./git-workflow-robustness.md)
