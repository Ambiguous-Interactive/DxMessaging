---
title: "Changelog Entry Writing and Anti-Patterns"
id: "changelog-entry-writing"
category: "documentation"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-03-17"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "CHANGELOG.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "changelog"
  - "release-notes"
  - "writing"
  - "keep-a-changelog"
  - "user-communication"
  - "anti-patterns"

complexity:
  level: "basic"
  reasoning: "Requires translating implementation details into user-facing impact"

impact:
  performance:
    rating: "none"
    details: "Documentation only, no runtime impact"
  maintainability:
    rating: "high"
    details: "Clear changelog entries reduce support burden and upgrade friction"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Understanding of changelog categories"

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
  - "Release note writing"
  - "Changelog entry style"

related:
  - "changelog-management"
  - "changelog-release-workflow"
  - "documentation-updates"

status: "stable"
---

# Changelog Entry Writing and Anti-Patterns

> **One-line summary**: Write concise, user-focused changelog entries that explain impact, not implementation details.

## Overview

A good changelog entry tells users what changed and why it matters. It is brief, specific, and written from the user's point of view. The goal is to help users decide whether to upgrade and understand how to adapt.

## Problem Statement

Poor entries cause confusion and support churn:

- "Fixed bugs" provides no actionable information
- Internal refactoring details do not explain user impact
- Missing links make it hard to track context
- Breaking changes are not clearly flagged

## Solution

### Core Concept

Write entries that are:

- **User-focused**: describe the effect, not the implementation
- **Actionable**: make it clear what users need to do
- **Traceable**: link to issues/PRs when possible
- **Explicit**: call out breaking changes and migrations

### Entry Template

```markdown
### Fixed

- Fixed <user-visible problem> when <context> ([#123](https://github.com/...))
```

## Usage Examples

### Example 1: Adding a New Message Type

```markdown
## [Unreleased]

### Added

- New `UntargetedInterceptableMessage` base class for broadcast messages that support
  interception and cancellation ([#142](https://github.com/Ambiguous-Interactive/DxMessaging/pull/142))
```

### Example 2: Fixing a Bug in Message Routing

```markdown
## [Unreleased]

### Fixed

- Messages are now correctly delivered to components on inactive GameObjects when
  using `IncludeInactive` delivery option ([#156](https://github.com/Ambiguous-Interactive/DxMessaging/issues/156))
```

### Example 3: Deprecating an Old API

```markdown
## [Unreleased]

### Deprecated

- `MessageBus.GlobalBus` is deprecated and will be removed in v3.0.0.
  Use `MessageBusProvider.Instance` instead for improved testability and lifecycle management
```

### Example 4: Breaking Change With Migration Notes

````markdown
## [3.0.0] - 2026-02-01

### Changed

- **BREAKING**: `IMessageHandler.Handle` now returns `MessageResult` instead of `void`
  to support cancellation. Update handlers to return `MessageResult.Continue` for
  existing behavior

### Removed

- **BREAKING**: Removed `MessageBus.GlobalBus`. Use `MessageBusProvider.Instance` instead
- **BREAKING**: Removed `ILegacyMessageHandler` interface deprecated in v2.5.0

### Migration

To upgrade from v2.x:

1. Replace `MessageBus.GlobalBus` with `MessageBusProvider.Instance`
2. Update `IMessageHandler` implementations to return `MessageResult`:

   ```csharp
   // Before (v2.x)
   public void Handle(ref MyMessage message) { /* ... */ }

   // After (v3.0)
   public MessageResult Handle(ref MyMessage message)
   {
       /* ... */
       return MessageResult.Continue;
   }
   ```
````

## Anti-Patterns

### Bad: Implementation Details Instead of User Impact

```markdown
### Changed

- Refactored MessageRouter to use Dictionary instead of List for O(1) lookup
```

**Why it's wrong**: Users do not care about internal data structures. Describe the benefit.

**Correct**:

```markdown
### Changed

- Improved message routing performance for buses with many handlers
```

### Bad: Vague or Generic Entries

```markdown
### Fixed

- Fixed bugs
- Various improvements
- Code cleanup
```

**Why it's wrong**: Provides no actionable information. Users cannot determine if their issue was fixed.

**Correct**:

```markdown
### Fixed

- Fixed NullReferenceException when emitting messages to destroyed GameObjects
- Fixed memory leak in MessageBus when used in Play Mode tests
```

### Bad: Internal Tooling Framing

```markdown
### Added

- Added automation script for agent prompt routing
```

**Why it's wrong**: This describes internal process/tooling, not user-facing product impact.

**Correct**:

```markdown
### Changed

- Improved AI-assistant onboarding for users by adding `llms.txt` and README guidance
  so package docs and APIs are discovered with accurate context
```

### Bad: Missing Breaking Change Warnings

```markdown
### Changed

- Updated Handle method signature
```

**Why it's wrong**: Does not indicate this is breaking or how to migrate.

**Correct**:

```markdown
### Changed

- **BREAKING**: `IMessageHandler.Handle` now requires a return value.
  See Migration section for upgrade instructions
```

### Bad: Not Linking Issues/PRs

```markdown
### Fixed

- Fixed the message ordering bug
```

**Why it's wrong**: No way to find more context or related discussion.

**Correct**:

```markdown
### Fixed

- Fixed message delivery order when using priority handlers
  ([#178](https://github.com/Ambiguous-Interactive/DxMessaging/issues/178))
```

### Bad: Updating Changelog After Release

```markdown
## [2.1.4] - 2026-01-15

### Fixed

- Fixed message ordering bug after release
```

**Why it's wrong**: Retroactively editing released notes breaks release traceability and can desync package release notes from what users saw at ship time.

**Correct**:

```markdown
## [Unreleased]

### Fixed

- Fixed message delivery order when using priority handlers
  ([#178](https://github.com/Ambiguous-Interactive/DxMessaging/issues/178))
```

## See Also

- [changelog entry writing part 1](./changelog-entry-writing-part-1.md)
