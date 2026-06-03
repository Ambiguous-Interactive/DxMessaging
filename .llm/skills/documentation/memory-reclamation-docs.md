---
title: "Memory Reclamation Documentation Maintenance"
id: "memory-reclamation-docs"
category: "documentation"
version: "1.0.0"
created: "2026-05-06"
updated: "2026-05-06"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs"
    - path: "Runtime/Core/Configuration/DxMessagingRuntimeSettingsProvider.cs"
    - path: "Runtime/Core/MessageBus/IMessageBus.cs"
    - path: "Runtime/Core/MessageBus/MessageHandler.cs"
    - path: "Runtime/Core/Pooling/DxPools.cs"
    - path: "docs/guides/memory-reclamation.md"
    - path: "docs/reference/runtime-settings.md"
    - path: "CHANGELOG.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "memory-reclamation"
  - "runtime-settings"
  - "changelog"
  - "maintenance"

complexity:
  level: "basic"
  reasoning: "Mechanical doc-update checklist tied to a fixed list of trigger files."

impact:
  performance:
    rating: "none"
    details: "Documentation only; runtime behavior is unaffected."
  maintainability:
    rating: "high"
    details: "Keeps the user-facing memory-reclamation surface and CHANGELOG aligned with the runtime."
  testability:
    rating: "low"
    details: "validate:runtime-settings-docs and validate:changelog:coverage cover the update mechanically."

prerequisites:
  - "memory-reclamation"
  - "memory-reclaim-coverage"
  - "changelog-management"

dependencies:
  packages: []
  skills:
    - "memory-reclamation"
    - "memory-reclaim-coverage"
    - "changelog-management"

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
  - "Memory reclamation docs"
  - "Runtime settings doc maintenance"

related:
  - "memory-reclamation"
  - "memory-reclaim-coverage"
  - "changelog-management"

status: "stable"
---

# Memory Reclamation Documentation Maintenance

> **One-line summary**: When changing memory-reclamation runtime behavior,
> update the user docs and CHANGELOG in the same change.

## When this skill applies

Trigger files. When any of these change, the user-facing memory-reclamation
docs are likely affected:

- `Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs`
- `Runtime/Core/Configuration/DxMessagingRuntimeSettingsProvider.cs`
- `Runtime/Core/MessageBus/IMessageBus.cs` (`Trim`, `OccupiedTypeSlots`,
  `OccupiedTargetSlots`, `TrimResult`)
- `Runtime/Core/MessageBus/MessageHandler.cs` (`TrimAll`)
- `Runtime/Core/Pooling/**`
- `Runtime/Core/Configuration/**`

Treat changes to public field names, default values, attribute thresholds, or
public method shapes on these files as user-visible by default.

## Required updates

When any trigger file changes, update IN THE SAME CHANGE:

1. `docs/guides/memory-reclamation.md` -- the narrative guide for tuning idle
   sweeps, forced trims, and pool caps.
1. `docs/reference/runtime-settings.md` -- the per-setting reference table that
   `validate:runtime-settings-docs` cross-references against
   `DxMessagingRuntimeSettings`.
1. `CHANGELOG.md` -- the existing `## [Unreleased]` "Runtime memory-reclamation
   foundations" bullet. Mutate the existing bullet rather than stacking a new
   one; see [Changelog Management](./changelog-management.md). When the change
   is a distinct user-facing fix that the bullet does not cover, add a single
   `### Fixed` line item instead of duplicating the foundations bullet.

## Validation

Run from the repository root:

```bash
npm run validate:runtime-settings-docs
npm run validate:changelog:coverage
```

If `validate:runtime-settings-docs` reports `missing-doc-row`, add the new
row to `docs/reference/runtime-settings.md` matching the shape of the
existing rows. If it reports `extra-doc-row`, remove the stale row because
the underlying setting was removed or renamed.

If `validate:changelog:coverage` raises `W002`, rewrite the entry around user
impact. Internal-only renames belong in developer docs, not in the changelog.

## See also

- [DxMessaging Memory Reclamation](../performance/memory-reclamation.md)
- [Memory Reclaim Coverage](../testing/memory-reclaim-coverage.md)
- [Changelog Management](./changelog-management.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-06 | Initial version |
