# Test Coverage Requirements

> **One-line summary**: Every new feature and bug fix requires tests covering happy paths, negative scenarios, edge cases, and "impossible" situations.

## Overview

Full test coverage is not optional. Every code change must include tests that verify:

1. The feature works as intended (happy path)
1. The feature handles errors gracefully (negative scenarios)
1. The feature behaves correctly at boundaries (edge cases)
1. The feature survives unexpected usage (unexpected situations)
1. The feature handles "impossible" scenarios defensively

## When Tests Are Required

| Scenario                 | Requirement                                                |
| ------------------------ | ---------------------------------------------------------- |
| New feature              | Tests for all public APIs                                  |
| Bug fix                  | Regression test that fails before fix, passes after        |
| Performance optimization | Benchmark tests proving improvement                        |
| Refactoring              | Existing tests must pass; add tests if coverage gaps exist |
| API change               | Update existing tests + add tests for new behavior         |

## Solution

### Core Concept

Design tests around **coverage categories** and **data-driven patterns**:

- Normal/happy path
- Negative/error conditions
- Edge/boundary cases
- Unexpected usage
- "Impossible" defensive cases

Use `[TestCase]` and `[TestCaseSource]` to consolidate coverage without duplication.

## See Also

- [Scenario Coverage Categories](./test-coverage-scenario-categories.md)
- [Data-Driven Coverage Patterns](./test-coverage-data-driven.md)
- [Organization and Assertions](./test-coverage-organization-assertions.md)
- [Unity Considerations and Anti-Patterns](./test-coverage-unity-anti-patterns.md)
- [Data-Driven Tests](../../data-driven-tests/references/data-driven-tests.md)
- [Test Failure Investigation](../../test-failure-investigation/references/test-failure-investigation.md)

## References

- NUnit Documentation: https://docs.nunit.org/
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@latest

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
