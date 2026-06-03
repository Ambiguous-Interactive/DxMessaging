---
title: "Changelog Release Workflow"
id: "changelog-release-workflow"
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
  - "release-workflow"
  - "versioning"
  - "automation"
  - "keep-a-changelog"

complexity:
  level: "basic"
  reasoning: "Requires consistent release discipline and correct version/link formatting"

impact:
  performance:
    rating: "none"
    details: "Documentation only, no runtime impact"
  maintainability:
    rating: "high"
    details: "Release workflow keeps changelog accurate and easy to navigate"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Understanding of semantic versioning"

dependencies:
  packages: []
  skills:
    - "changelog-management"

applies_to:
  languages:
    - "Markdown"
  frameworks:
    - "Unity"
    - ".NET"

aliases:
  - "Release notes workflow"
  - "Changelog release process"

related:
  - "changelog-management"
  - "changelog-entry-writing"
  - "documentation-updates"

status: "stable"
---

# Changelog Release Workflow

> **One-line summary**: Keep the changelog accurate through a disciplined Unreleased workflow, consistent version formatting, and correct link updates.

## Overview

A changelog is only useful if the Unreleased section is maintained during development and converted correctly during releases. This skill defines how to manage Unreleased entries, cut a release, and keep version links accurate.

## Problem Statement

Common release mistakes include:

- Unreleased items never moved to a versioned section
- Missing or inconsistent dates and version formats
- Broken compare links at the bottom of the file
- Mixing per-package changes in a single changelog without structure

## Solution

### Unreleased Section Workflow

1. **During development**: Add entries to `[Unreleased]` as changes are made
1. **Before release**: Move `[Unreleased]` content to a new version section
1. **At release**: Add date and version links
1. **After release**: Create a fresh, empty `[Unreleased]` section

#### Converting to a Release

**Before**:

```markdown
## [Unreleased]

### Added

- New feature X
```

**After**:

```markdown
## [Unreleased]

## [2.2.0] - 2026-01-22

### Added

- New feature X
```

### Date and Version Formats

Use ISO 8601 dates and semantic versions in brackets:

```markdown
## [2.1.4] - 2026-01-22
```

- Valid: `[2.1.4]`
- Valid: `[3.0.0]`
- Invalid: `v2.1.4` (no 'v' prefix in header)
- Invalid: `2.1.4` (missing brackets)

### Version Links

Add reference-style links at the bottom of the file:

```markdown
[Unreleased]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.4...HEAD
[2.1.4]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.3...v2.1.4
[2.1.3]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.2...v2.1.3
[2.1.2]: https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/v2.1.2
```

Note: The oldest version links to its release tag, not a comparison.

## Variations

### Variation A: Single CHANGELOG.md (Recommended)

```text
repository/
|- CHANGELOG.md
|- package.json
`- ...
```

**When to use**: Most projects, especially libraries and packages.

### Variation B: Per-Package Changelogs

```text
repository/
|- packages/
|  |- core/
|  |  |- CHANGELOG.md
|  |  `- package.json
|  `- unity/
|     |- CHANGELOG.md
|     `- package.json
`- CHANGELOG.md
```

**When to use**: Monorepos with multiple published packages.

### Variation C: GitHub Releases as Changelog

**Pros**:

- Integrated with release workflow
- Links naturally to commits and PRs
- Supports assets and binaries

**Cons**:

- Harder to view historical changes
- Not portable (tied to GitHub)
- Less control over formatting

### Variation D: Generated Changelogs

Tools like `conventional-changelog` can generate changelogs from commit messages:

```bash
npx conventional-changelog -p angular -i CHANGELOG.md -s
```

**When to use**: Teams with strict commit message conventions who want automation.

## Usage Examples

### Example: Complete Version Entry

```markdown
## [2.2.0] - 2026-01-20

### Added

- `MessageBus.EmitAsync` for asynchronous message delivery in non-Unity contexts
- `IMessageInterceptor` interface for pre-processing messages before delivery
- Support for message priority ordering via `[MessagePriority]` attribute
- New `PooledMessage<T>` base class for zero-allocation messaging patterns

### Changed

- Improved broadcast message routing performance by 25% through internal caching
- `MessageRegistrationToken` now implements `IEquatable<MessageRegistrationToken>`

### Fixed

- Fixed race condition in `MessageBus.Dispose` when called during active message delivery
- Corrected memory leak when repeatedly registering/unregistering handlers in Editor mode

### Deprecated

- `MessageBus.RegisterHandler(Type, Delegate)` - use generic `RegisterHandler<T>` instead
```

## Testing Considerations

While changelogs are not unit-tested, validate format in CI:

```yaml
- name: Validate Changelog
  run: |
    grep -q "## \[Unreleased\]" CHANGELOG.md
    PACKAGE_VERSION=$(jq -r .version package.json)
    grep -q "## \[${PACKAGE_VERSION}\]" CHANGELOG.md
```

## See Also

- [Changelog Management](changelog-management.md)
- [Changelog Entry Writing](changelog-entry-writing.md)
- [Documentation Updates](documentation-updates.md)

## References

- [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
