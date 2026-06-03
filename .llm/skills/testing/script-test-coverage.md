---
title: "Script Test Coverage Requirements"
id: "script-test-coverage"
category: "testing"
version: "1.0.0"
created: "2026-01-28"
updated: "2026-01-28"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/__tests__/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "scripts"
  - "jest"
  - "coverage"
  - "javascript"
  - "powershell"
  - "ci-cd"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of test architecture for scripts"

impact:
  performance:
    rating: "none"
    details: "Test patterns only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Proper test coverage catches issues before CI"
  testability:
    rating: "critical"
    details: "Defines testing standards for all project scripts"

prerequisites:
  - "Jest testing framework familiarity"
  - "Understanding of JavaScript module testing"
  - "Basic PowerShell knowledge for testing PS logic"

dependencies:
  packages:
    - "jest"
  skills:
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "JavaScript"
    - "PowerShell"
  frameworks:
    - "Jest"
    - "Node.js"
  versions:
    node: ">=18.0"
    jest: ">=29.0"

aliases:
  - "Script testing"
  - "Test coverage for scripts"

related:
  - "cross-platform-compatibility"
  - "comprehensive-test-coverage"

status: "stable"
---

# Script Test Coverage Requirements

> **One-line summary**: All scripts in `scripts/` must have corresponding test coverage in
> `scripts/__tests__/` to catch bugs before CI.

## Overview

Every script in the project must have corresponding test coverage. This catches bugs before CI,
including case sensitivity issues, regex pattern errors, and logic flaws. Tests are written in
JavaScript using Jest, even when testing PowerShell script logic.

## Solution

1. **Create test files** in `scripts/__tests__/` for each script
1. **Implement equivalent JavaScript functions** to test PowerShell logic
1. **Verify file paths** to catch case sensitivity issues
1. **Test edge cases** -- empty input, malformed input, missing files, and unexpected error paths
1. **Use `test()` not `it()`** for all Jest test declarations

## Test Location and Naming

```text
scripts/
+-- sync-banner-version.ps1      # PowerShell script
+-- fix-eol.js                   # JavaScript script
+-- __tests__/
    +-- sync-banner-version.test.js  # Tests for PowerShell logic
    +-- fix-eol.test.js              # Tests for JavaScript logic
```

### Naming Convention

- Test files must be named `<script-name>.test.js`
- Place all test files in `scripts/__tests__/`
- Test file names should match their source script names

## What to Test

For each script, test:

1. **Core logic functions** - Extract testable functions and verify behavior
1. **Input validation** - Invalid inputs, missing files, malformed data
1. **Edge cases** - Empty files, special characters, boundary conditions
1. **Error handling** - Graceful failures with informative messages
1. **File path references** - Verify paths exist with correct case

## Testing PowerShell Logic in JavaScript

When the actual script is PowerShell, implement equivalent JavaScript functions for testing:

```javascript
/**
 * @fileoverview Tests for sync-banner-version.ps1 logic.
 *
 * Since the actual script is PowerShell, we test equivalent JavaScript
 * implementations of the core logic to ensure correctness.
 */

// SYNC: Keep pattern in sync with sync-banner-version.ps1
const VERSION_PATTERN = /<!-- Version badge.*?-->\s*<g[^>]*>.*?<\/g>/s;

function extractVersion(packageJsonContent) {
  // Equivalent to PowerShell version extraction logic
  const parsed = JSON.parse(packageJsonContent);
  return parsed.version || null;
}

describe("Version Extraction", () => {
  test("should extract valid semver version", () => {
    const content = '{"version": "1.2.3"}';
    expect(extractVersion(content)).toBe("1.2.3");
  });

  test("should return null for missing version", () => {
    const content = '{"name": "test"}';
    expect(extractVersion(content)).toBeNull();
  });
});
```

## File Path Testing Pattern

When scripts reference files, test that paths are correct:

```javascript
const fs = require("fs");
const path = require("path");

describe("File References", () => {
  test("banner file exists with expected case", () => {
    // Test the exact path used in the PowerShell script
    const bannerPath = path.join(__dirname, "../../docs/images/DxMessaging-banner.svg");
    expect(fs.existsSync(bannerPath)).toBe(true);
  });

  test("banner filename case matches script reference", () => {
    const imagesDir = path.join(__dirname, "../../docs/images");
    const files = fs.readdirSync(imagesDir);
    const bannerFile = files.find((f) => f.toLowerCase().includes("banner"));

    // Verify exact case matches what scripts expect
    expect(bannerFile).toBe("DxMessaging-banner.svg");
  });
});
```

## Jest Test Style Consistency

### Use `test()` Not `it()`

This project uses `test()` for all Jest test declarations. Do not use `it()` even though
Jest treats them as equivalent.

#### Rationale

1. **Consistency** - All existing tests use `test()`
1. **Clarity** - `test()` is more explicit about purpose
1. **Searchability** - Single pattern makes grep/search easier

#### Examples

```javascript
// CORRECT: Use test()
describe("Version Extraction", () => {
  test("should extract valid semver version", () => {
    expect(extractVersion('{"version": "1.0.0"}')).toBe("1.0.0");
  });

  test("should handle pre-release versions", () => {
    expect(extractVersion('{"version": "1.0.0-beta.1"}')).toBe("1.0.0-beta.1");
  });
});

// WRONG: Do not use it()
describe("Version Extraction", () => {
  it("should extract valid semver version", () => {
    // ...
  });
});
```

#### Nested Describes

For nested `describe` blocks, continue using `test()`:

```javascript
describe("SVG Processing", () => {
  describe("Version Badge", () => {
    test("should find version badge in valid SVG", () => {
      // ...
    });

    test("should return null when badge missing", () => {
      // ...
    });
  });

  describe("Error Handling", () => {
    test("should handle empty content", () => {
      // ...
    });
  });
});
```

## Test Coverage Validation Checklist

Before merging scripts:

- [ ] JavaScript test file exists in `scripts/__tests__/`
- [ ] Tests cover core logic functions
- [ ] Tests verify file path correctness
- [ ] Tests include edge cases and error scenarios
- [ ] All tests use `test()` not `it()`
- [ ] Test descriptions are clear and specific
- [ ] `describe` blocks group related tests logically

## See Also

- [Cross-Platform Compatibility](../scripting/cross-platform-compatibility.md) - Case sensitivity patterns
- [Test Coverage Requirements](./comprehensive-test-coverage.md) - General test coverage requirements
- [PowerShell Best Practices](../scripting/powershell-best-practices.md) - PowerShell scripting patterns

## Changelog

| Version | Date       | Changes                                        |
| ------- | ---------- | ---------------------------------------------- |
| 1.0.0   | 2026-01-28 | Extracted from cross-platform-compatibility.md |
