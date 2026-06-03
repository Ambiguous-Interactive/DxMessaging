---
title: "Changelog Management"
id: "changelog-management"
category: "documentation"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "CHANGELOG.md"
    - path: "package.json"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "changelog"
  - "documentation"
  - "versioning"
  - "semantic-versioning"
  - "release-notes"
  - "keep-a-changelog"
  - "user-communication"

complexity:
  level: "basic"
  reasoning: "Follows a standard format (Keep a Changelog) with clear rules for entry classification"

impact:
  performance:
    rating: "none"
    details: "Documentation only, no runtime impact"
  maintainability:
    rating: "high"
    details: "Well-maintained changelogs dramatically improve project maintainability and reduce support burden"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Basic understanding of semantic versioning"
  - "Understanding of what constitutes a user-facing change"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "C#"
    - "Markdown"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "Release notes"
  - "Version history"
  - "Change log"

related:
  - "documentation-updates"
  - "changelog-entry-writing"
  - "changelog-release-workflow"

status: "stable"
---

# Changelog Management

> **One-line summary**: Maintain a human-readable, chronologically organized record of notable changes following the Keep a Changelog format.

## Overview

A changelog is a curated, chronological record of user-facing changes for each release. It helps users understand what changed, whether a version is safe to adopt, and how to plan upgrades without digging through commits.

In a Unity messaging library context, the changelog is the primary source of truth for:

- New features that users can adopt
- Breaking changes to watch for
- Fixes that resolve user-visible bugs
- Performance changes that affect runtime behavior

## Problem Statement

Without a well-maintained changelog:

- Users cannot tell what changed between versions
- Breaking changes surprise users after upgrading
- Support burden increases as users ask "what's new?"
- Historical context for decisions is lost

## Solution

### Core Concept: Keep a Changelog Format

Follow the [Keep a Changelog](https://keepachangelog.com/) specification. Every release section uses these categories:

| Category       | Description                       | Example                                    |
| -------------- | --------------------------------- | ------------------------------------------ |
| **Added**      | New features                      | New message type, new API method           |
| **Changed**    | Changes to existing functionality | Modified method signature, behavior change |
| **Deprecated** | Features to be removed in future  | Old API marked for removal                 |
| **Removed**    | Features removed in this release  | Deleted obsolete classes                   |
| **Fixed**      | Bug fixes                         | Corrected message routing issue            |
| **Security**   | Vulnerability patches             | Fixed message validation exploit           |

### File Structure

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- New feature being developed

## [2.1.4] - 2026-01-15

### Fixed

- Resolved issue with targeted message delivery when target is destroyed

[Unreleased]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.4...HEAD
```

### When to Update the Changelog

Update the changelog for **every user-facing change**:

| Change Type                          | Update Changelog? | Category                   |
| ------------------------------------ | ----------------- | -------------------------- |
| New public API                       | Yes               | Added                      |
| Bug fix                              | Yes               | Fixed                      |
| Performance improvement              | Yes               | Changed                    |
| Internal refactoring (no API change) | No                | N/A                        |
| Test additions                       | No                | N/A                        |
| Documentation fixes                  | Maybe             | Changed (if significant)   |
| Dependency updates                   | Maybe             | Changed (if affects users) |
| Breaking changes                     | Yes               | Changed/Removed            |

### Semantic Versioning Guidelines

Given version `MAJOR.MINOR.PATCH`:

```text
MAJOR.MINOR.PATCH
  |     |     \\-- Patch: Bug fixes, no API changes
  |     \\-------- Minor: New features, backward compatible
  \\-------------- Major: Breaking changes
```

- **Bump MAJOR** when removing public API or changing behavior incompatibly
- **Bump MINOR** when adding backward-compatible features or deprecating APIs
- **Bump PATCH** when fixing bugs or making compatible performance improvements

### Entry Quality Rules

- Describe user impact, not implementation details
- Start entries with a clear verb (Add, Fix, Change, Remove)
- Link issues or PRs when available
- Flag breaking changes explicitly with migration guidance

## Anti-Patterns

### Anti-Pattern 1: Internal Tooling or AI Agent Documentation

```markdown
<!-- WRONG -->

### Added

- MkDocs navigation skill documenting navigation patterns
```

Changelog entries describe changes that **users experience**, not internal tooling, AI agent guidance, or developer-facing documentation. Skills, context files, and internal process documentation are invisible to package consumers and do not belong in the changelog.

**What to document instead**: Changes to user-facing documentation (API docs, tutorials, README) may warrant changelog entries if they significantly improve the user experience.

### Anti-Pattern 2: Overly Broad Scope

```markdown
<!-- WRONG -->

### Changed

- Updated test assembly definitions
```

This entry implies all test assembly definitions were changed, which is rarely accurate. Entries must accurately scope what was actually affected.

```markdown
<!-- CORRECT -->

### Fixed

- `WallstopStudios.DxMessaging.Tests.Runtime` assembly definition now specifies Editor-only platform to prevent Burst compilation errors during player builds
```

**Rule**: Be specific about what changed. Name the exact files, assemblies, or components that were modified. Vague entries create confusion about which components were affected and make it harder to trace issues back to specific changes.

## See Also

- [Changelog Entry Writing](changelog-entry-writing.md)
- [Changelog Release Workflow](changelog-release-workflow.md)
- [Documentation Updates](documentation-updates.md)
- [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)

## References

- [Keep a Changelog Specification](https://keepachangelog.com/en/1.1.0/)
- [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
