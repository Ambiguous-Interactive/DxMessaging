---
title: "Test Failure Investigation and Zero-Flaky Policy"
id: "test-failure-investigation"
category: "testing"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "investigation"
  - "debugging"
  - "quality"
  - "zero-flaky"
  - "root-cause-analysis"

complexity:
  level: "intermediate"
  reasoning: "Requires systematic debugging skills and understanding of production behavior"

impact:
  performance:
    rating: "none"
    details: "Investigation process; no runtime impact"
  maintainability:
    rating: "critical"
    details: "Ensures codebase quality and prevents hidden bugs"
  testability:
    rating: "critical"
    details: "Maintains test suite reliability and trustworthiness"

prerequisites:
  - "Understanding of debugging techniques"
  - "Familiarity with the codebase under test"

dependencies:
  packages: []
  skills:
    - "test-diagnostics"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "Flaky test investigation"
  - "Test debugging"
  - "Root cause analysis"
  - "Zero-flaky policy"

related:
  - "test-diagnostics"
  - "test-base-class-cleanup"
  - "test-categories"
  - "test-failure-investigation-procedure"
  - "test-failure-investigation-root-causes"

status: "stable"
---

# Test Failure Investigation and Zero-Flaky Policy

> **One-line summary**: Every test failure reveals a real bug - investigate the production code path, the test setup, and any shared static state before making any fix.

## Overview

This project maintains a **zero-flaky test policy**. A flaky test is one that sometimes passes and sometimes fails without code changes. We do not tolerate flaky tests because they hide real bugs and erode trust in the test suite.

### Every test failure must be treated as a production bug until proven otherwise

## Problem Statement

Ignoring or masking test failures leads to unreliable tests, hidden regressions, and broken production behavior.

## Solution

### Core Principles

1. **All test failures indicate bugs**: Production or test code - both require fixes.
1. **No superficial fixes**: Never "make the test pass" without understanding why it failed.
1. **No ignored tests**: Do not use `[Ignore]`, `[Skip]`, or commented-out tests to hide failures.
1. **Full investigation required**: Find the root cause before making changes.
1. **Document discoveries**: Captured edge cases become institutional knowledge.

### High-Level Investigation Flow

1. Reproduce the failure reliably
1. Understand expected behavior and assertions
1. Inspect production code paths
1. Identify root cause (production vs test)
1. Fix the production or test root cause and verify the fix holds across repeated runs

## Summary

A passing test suite should mean the code works correctly. A failing test should mean there is a real problem to fix. This trust is essential for effective development.

## See Also

- [Investigation Procedure](test-failure-investigation-procedure.md)
- [Root Causes and Anti-Patterns](test-failure-investigation-root-causes.md)
- [Test Diagnostics](test-diagnostics.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
