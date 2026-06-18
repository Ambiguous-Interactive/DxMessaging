---
title: "GitHub Actions Version Consistency"
id: "github-actions-version-consistency"
category: "documentation"
version: "1.0.0"
created: "2026-01-27"
updated: "2026-06-18"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows/"
    - path: ".github/workflows-disabled/"
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
# GOOD: Use repo-wide pinned major versions consistently.
- uses: actions/checkout@v6
- uses: actions/setup-node@v6
- uses: actions/upload-artifact@v7

# BAD: Mixed versions across workflows
- uses: actions/checkout@v5 # One workflow
- uses: actions/checkout@v6 # Another workflow
```

### Version Update Process

1. **Audit all workflows**: Find all action uses across `.github/workflows/`
   and `.github/workflows-disabled/`
1. **Identify inconsistencies**: List actions with different versions
1. **Verify upstream tags**: Check official repository tags/releases before
   accepting or rejecting a version bump
1. **Update together**: Change all instances in a single PR
1. **Test thoroughly**: Run all affected workflows before merging

### Common Actions to Monitor

Do not treat a static table as permanent truth. Verify the official action tag
before claiming that a workflow version is unpublished. As of 2026-06-18, this
repo intentionally uses these published official-action majors in enabled and
disabled workflow files:

| Action                            | Repo Pin | Notes                               |
| --------------------------------- | -------- | ----------------------------------- |
| `actions/attest-build-provenance` | `v4`     | Build provenance attestations       |
| `actions/checkout`                | `v6`     | Checkout and credential handling    |
| `actions/cache`                   | `v5`     | Dependency and Unity project caches |
| `actions/cache/restore`           | `v5`     | Link-check cache restore            |
| `actions/cache/save`              | `v5`     | Link-check cache save               |
| `actions/create-github-app-token` | `v3`     | GitHub App authentication           |
| `actions/deploy-pages`            | `v5`     | Pages deployments                   |
| `actions/download-artifact`       | `v8`     | Workflow artifact downloads         |
| `actions/github-script`           | `v9`     | GitHub API scripting                |
| `actions/setup-dotnet`            | `v5`     | .NET SDK setup                      |
| `actions/setup-node`              | `v6`     | Node.js setup                       |
| `actions/setup-python`            | `v6`     | Python setup                        |
| `actions/upload-artifact`         | `v7`     | Workflow artifact uploads           |
| `actions/upload-pages-artifact`   | `v5`     | Pages artifact uploads              |

### Audit Command

```bash
# Find all action versions in workflows.
rg -n "uses:\s+[^[:space:]]+@v[0-9]+" .github/workflows .github/workflows-disabled

# Verify an official major tag exists before flagging it as invalid.
git ls-remote --tags https://github.com/actions/setup-node.git refs/tags/v6
```

### Artifact Action Pairing

Upload and download artifact actions must use compatible versions. Compatibility
can span different major numbers, so verify release notes instead of requiring
the numbers to match blindly:

```yaml
# GOOD: Compatible published artifact actions
- uses: actions/upload-artifact@v7
  # ... later in workflow or different job ...
- uses: actions/download-artifact@v8

# BAD: Mixing an obsolete/deprecated artifact generation with current downloads
- uses: actions/upload-artifact@v3
- uses: actions/download-artifact@v8
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

### Do Not Write New Bespoke Documentation Linters

The repository deliberately uses off-the-shelf linters (markdownlint-cli2, prettier, cspell, lychee) instead of bespoke documentation-linter scripts. See the Tooling Philosophy section of `.llm/context.md` before adding any new linting script; the code-block-skipping guidance above exists only for the rare case where an existing kept script must be modified.

## Validation Checklist

Before committing workflow changes:

- [ ] All action versions are consistent across workflows
- [ ] Upload/download artifact versions are compatible
- [ ] Target major tags exist in the official upstream action repository
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
