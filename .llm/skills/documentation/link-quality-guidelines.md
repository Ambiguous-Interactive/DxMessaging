---
title: "Link Quality and External URL Management"
id: "link-quality-guidelines"
category: "documentation"
version: "1.4.0"
created: "2026-01-22"
updated: "2026-02-09"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
    - path: ".llm/"
    - path: ".github/workflows/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "links"
  - "urls"
  - "markdown"
  - "ci-cd"
  - "quality"
  - "accessibility"
  - "github-actions"
  - "linting"
  - "testing"

complexity:
  level: "basic"
  reasoning: "Link quality follows clear patterns but requires attention to detail and verification"

impact:
  performance:
    rating: "none"
    details: "Documentation links do not affect runtime performance"
  maintainability:
    rating: "high"
    details: "Broken or unclear links degrade documentation quality and user experience"
  testability:
    rating: "low"
    details: "Link validation can be automated but is not part of core test suite"

prerequisites:
  - "Understanding of Markdown link syntax"
  - "Familiarity with GitHub repository structure"
  - "Basic knowledge of GitHub Actions"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "Markdown"
    - "YAML"
  frameworks:
    - "GitHub Actions"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "URL management"
  - "Link validation"
  - "Markdown links"
  - "External references"

related:
  - "documentation-updates"
  - "changelog-management"
  - "documentation-style-guide"
  - "external-url-fragment-validation"
  - "github-actions-version-consistency"

status: "stable"
---

# Link Quality and External URL Management

> **One-line summary**: Ensure all links use human-readable text, point to correct URLs, and remain valid over time.

## Overview

Links in documentation serve two purposes: navigation and context. Poor link quality -- whether through cryptic text, incorrect URLs, or broken references -- damages user trust and wastes developer time investigating CI failures.

This skill covers:

- Writing human-readable link text
- Ensuring repository URL consistency in skill files
- Validating external links before committing
- Keeping GitHub Action versions consistent

## Problem Statement

Link-related issues cause preventable CI/CD failures and documentation quality problems:

| Issue Type                     | Impact                                         | Example                                                     |
| ------------------------------ | ---------------------------------------------- | ----------------------------------------------------------- |
| Non-descriptive link text      | Poor accessibility, confusing navigation       | `[README.md](../README.md)` vs `[the README](../README.md)` |
| Incorrect repository URLs      | Broken skill file validation, wrong references | Using wrong org/repo in frontmatter                         |
| Broken external URLs           | 404 errors, outdated documentation references  | Linking to deprecated Unity docs pages                      |
| Workflow version inconsistency | Unpredictable CI behavior, security issues     | Mixing `actions/checkout@v3` and `actions/checkout@v4`      |

## Solution

Refer to the detailed implementation guides linked below, which cover:

- implementation strategy and data structures
- code examples with patterns and variations
- usage examples and testing considerations
- performance notes and anti-patterns

## See Also

- [link quality guidelines part 1](./link-quality-guidelines-part-1.md)
- [link quality guidelines part 2](./link-quality-guidelines-part-2.md)
