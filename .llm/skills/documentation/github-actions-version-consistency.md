---
title: "GitHub Actions Version Consistency"
id: "github-actions-version-consistency"
category: "documentation"
version: "1.0.0"
created: "2026-01-27"
updated: "2026-01-27"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "ci-cd"
  - "version-management"
  - "workflows"
  - "linting"

complexity:
  level: "basic"
  reasoning: "Version consistency follows clear patterns and can be easily audited"

impact:
  performance:
    rating: "none"
    details: "Action versions do not affect runtime performance"
  maintainability:
    rating: "high"
    details: "Inconsistent versions cause unpredictable CI behavior"
  testability:
    rating: "medium"
    details: "Can be validated with grep patterns and CI checks"

prerequisites:
  - "Understanding of GitHub Actions workflow syntax"
  - "Familiarity with semantic versioning"

dependencies:
  packages: []
  skills:
    - "link-quality-guidelines"

applies_to:
  languages:
    - "YAML"
  frameworks:
    - "GitHub Actions"

aliases:
  - "Action version management"
  - "Workflow version consistency"
  - "CI version alignment"

related:
  - "link-quality-guidelines"
  - "documentation-updates"

status: "stable"
---

# GitHub Actions Version Consistency

> **One-line summary**: Ensure all GitHub Actions workflows use consistent action versions across the repository.

## Overview

Workflow files should use consistent action versions across all workflows. Mixed versions can cause unpredictable CI behavior, security issues, and maintenance headaches.

## Problem Statement

Version inconsistencies cause preventable issues:

| Issue                     | Impact                                    | Example                                 |
| ------------------------- | ----------------------------------------- | --------------------------------------- |
| Mixed major versions      | Different behavior across workflows       | `checkout@v3` vs `checkout@v4`          |
| Outdated security patches | Vulnerability exposure                    | Using `v3` when `v4` has security fixes |
| Breaking change surprises | Unexpected failures after partial updates | Updating one workflow but not others    |
| Artifact version mismatch | Upload/download incompatibility           | `upload-artifact@v3` + `download@v4`    |

## Solution

### Version Format Standards

```yaml
# GOOD: Use the same major version consistently
- uses: actions/checkout@v4
- uses: actions/setup-dotnet@v4
- uses: actions/upload-artifact@v4

# BAD: Mixed versions across workflows
- uses: actions/checkout@v3 # One workflow
- uses: actions/checkout@v4 # Another workflow
```

### Version Update Process

1. **Audit all workflows**: Find all action uses across `.github/workflows/`
1. **Identify inconsistencies**: List actions with different versions
1. **Update together**: Change all instances in a single PR
1. **Test thoroughly**: Run all affected workflows before merging

### Common Actions to Monitor

| Action                      | Current Recommended | Notes                              |
| --------------------------- | ------------------- | ---------------------------------- |
| `actions/checkout`          | `v4`                | Breaking changes from v3           |
| `actions/setup-dotnet`      | `v4`                | .NET SDK setup                     |
| `actions/upload-artifact`   | `v4`                | Breaking changes from v3           |
| `actions/download-artifact` | `v4`                | Must match upload-artifact version |
| `actions/cache`             | `v4`                | Caching for dependencies           |

### Audit Command

```bash
# Find all action versions in workflows
grep -rh "uses:" .github/workflows/ | sort | uniq
```

### Artifact Action Pairing

Upload and download artifact actions must use compatible versions:

```yaml
# GOOD: Matching versions
- uses: actions/upload-artifact@v4
  # ... later in workflow or different job ...
- uses: actions/download-artifact@v4

# BAD: Mismatched versions (v4 upload with v3 download)
- uses: actions/upload-artifact@v4
- uses: actions/download-artifact@v3 # May not find v4 artifacts
```

## Documentation Linting Scripts

Automated link validation prevents broken links from reaching production. These scripts require careful implementation.

### Linting Scripts Must Skip Code Blocks

Documentation linters that check for raw file names or other patterns **must skip content inside code blocks**:

- **Fenced code blocks**: Content between ` ``` ` markers
- **Inline code**: Content between single backticks

Without this, examples showing anti-patterns will trigger false positives:

```markdown
<!-- This anti-pattern example would trigger a linter without code block handling -->

Bad: `See [README.md](../README.md)` <- Inline code, should be skipped
```

### Linting Scripts Need Unit Tests

Documentation linting scripts are code and need tests like any other code:

| Test Category        | Examples                                      |
| -------------------- | --------------------------------------------- |
| Normal patterns      | Valid links with good text                    |
| Anti-patterns        | Raw file names that should be flagged         |
| Edge cases           | Inline code, fenced blocks, nested structures |
| False positive cases | Anti-pattern examples in documentation        |
| Boundary conditions  | Empty files, single-line files, no links      |

### Script Implementation Checklist

When creating or modifying documentation linters:

- [ ] Skip content in fenced code blocks (` ``` `)
- [ ] Skip content in inline code (backticks)
- [ ] Handle nested structures correctly
- [ ] Include unit tests covering edge cases
- [ ] Test against existing documentation files
- [ ] Document expected behavior in script comments

## Validation Checklist

Before committing workflow changes:

- [ ] All action versions are consistent across workflows
- [ ] Upload/download artifact versions match
- [ ] No outdated major versions (check for v4 availability)
- [ ] Breaking changes reviewed when upgrading major versions
- [ ] All affected workflows tested after version updates

## See Also

- [Link Quality Guidelines](link-quality-guidelines.md) - Main link quality skill
- [Documentation Updates](documentation-updates.md) - Keeping docs in sync

## References

- [GitHub Actions - Using Actions](https://docs.github.com/en/actions/using-workflows/workflow-syntax-for-github-actions#jobsjob_idstepsuses)
- [GitHub Actions Changelog](https://github.blog/changelog/label/actions/)

## Changelog

| Version | Date       | Changes                                              |
| ------- | ---------- | ---------------------------------------------------- |
| 1.0.0   | 2026-01-27 | Split from link-quality-guidelines for focused scope |
