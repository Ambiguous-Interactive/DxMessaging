## Overview

Continuation material extracted from `test-production-code.md` to keep .llm files within the 300-line budget.

## Solution

### Structure Production Code for Testability

Export validation functions and helper logic so tests can import them:

```javascript
// PRODUCTION: scripts/validate-data.js
/**
 * Validates a record and returns any errors.
 * @param {Object} record - The record to validate
 * @returns {string[]} Array of error messages
 */
function validateRecord(record) {
  const errors = [];
  if (!isValidName(record.name)) {
    errors.push("Name must be at least 3 characters");
  }
  if (!isValidEmail(record.email)) {
    errors.push("Email must be valid");
  }
  return errors;
}

/**
 * Validates name length.
 * @param {string} name - The name to validate
 * @returns {boolean} True if valid
 */
function isValidName(name) {
  return name && name.length >= 3;
}

/**
 * Validates email format.
 * @param {string} email - The email to validate
 * @returns {boolean} True if valid
 */
function isValidEmail(email) {
  return email && email.includes("@");
}

// Export everything tests need
module.exports = {
  validateRecord,
  isValidName,
  isValidEmail
};
```

### Test Production Code Directly

Import and test the actual production functions:

```javascript
// TEST: scripts/__tests__/validate-data.test.js
// CORRECT: Import production code
const { validateRecord, isValidName, isValidEmail } = require("../validate-data");

describe("validateRecord", () => {
  test("should return no errors for valid record", () => {
    const result = validateRecord({ name: "John", email: "john@example.com" });
    expect(result).toHaveLength(0);
  });

  test("should return error for short name", () => {
    const result = validateRecord({ name: "Jo", email: "john@example.com" });
    expect(result).toContain("Name must be at least 3 characters");
  });

  test("should return error for invalid email", () => {
    const result = validateRecord({ name: "John", email: "invalid" });
    expect(result).toContain("Email must be valid");
  });
});

describe("isValidName", () => {
  test("should accept names with 3 or more characters", () => {
    expect(isValidName("Joe")).toBe(true);
    expect(isValidName("John")).toBe(true);
  });

  test("should reject names with fewer than 3 characters", () => {
    expect(isValidName("Jo")).toBe(false);
    expect(isValidName("J")).toBe(false);
  });

  test("should reject null and undefined", () => {
    expect(isValidName(null)).toBe(false);
    expect(isValidName(undefined)).toBe(false);
  });
});
```

## When Local Test Helpers Are Acceptable

Not all code in test files is duplication. These are legitimate uses:

### Thin Wrappers for Test Convenience

```javascript
// ACCEPTABLE: Thin wrapper that delegates to production
function validateAndExpectErrors(record, expectedCount) {
  const result = validateRecord(record); // Calls production!
  expect(result).toHaveLength(expectedCount);
  return result;
}
```

### Test-Only Utilities

```javascript
// ACCEPTABLE: Test data generators
function createValidRecord(overrides = {}) {
  return {
    name: "Default Name",
    email: "default@example.com",
    ...overrides
  };
}

// ACCEPTABLE: Custom matchers
function expectValidationError(result, expectedMessage) {
  expect(result.some((msg) => msg.includes(expectedMessage))).toBe(true);
}

// ACCEPTABLE: Factories for test data
const TestRecords = {
  valid: { name: "John Doe", email: "john@example.com" },
  invalidName: { name: "X", email: "john@example.com" }
};
```

## Handling Unavoidable Duplication

Some situations require maintaining parallel implementations. Use SYNC notes to keep them aligned.

### PowerShell Scripts with JavaScript Tests

When the production script is PowerShell but tests are JavaScript:

```powershell
# PRODUCTION: scripts/validate-data.ps1
# SYNC: Keep validation logic in sync with validate-data.test.js validateRecord()
function Test-Record {
    param([hashtable]$Record)
    $errors = @()
    if (-not $Record.name -or $Record.name.Length -lt 3) {
        $errors += "Name must be at least 3 characters"
    }
    return $errors
}
```

```javascript
// TEST: scripts/__tests__/validate-data.test.js
// SYNC: Keep validation logic in sync with validate-data.ps1 Test-Record
function validateRecord(record) {
  const errors = [];
  if (!record.name || record.name.length < 3) {
    errors += "Name must be at least 3 characters";
  }
  return errors;
}
```

### Browser-Only Code

When code runs only in browsers and cannot be tested in Node.js:

```javascript
// PRODUCTION: browser-only.js (uses DOM APIs)
// SYNC: Core logic duplicated for testing in browser-only.test.js

// TEST: browser-only.test.js
// SYNC: Keep validation logic in sync with browser-only.js computeLayout()
// This is a test-only implementation because production uses DOM APIs
```

## See Also

- [Test Production Code Directly](./test-production-code.md)
