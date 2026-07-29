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
   one; see [Changelog Management](../../changelog-management/references/changelog-management.md). When the change
   is a distinct user-facing fix that the bullet does not cover, add a single
   `### Fixed` line item instead of duplicating the foundations bullet.

## Validation

There is no automated drift gate; review by hand:

- Every setting in `DxMessagingRuntimeSettings` has a matching row in
  `docs/reference/runtime-settings.md` (add missing rows in the shape of the
  existing ones; remove rows for settings that were removed or renamed).
- `docs/guides/memory-reclamation.md` reflects the same change.
- `CHANGELOG.md` is updated when the change is user-visible.

If `validate:changelog:coverage` raises `W002`, rewrite the entry around user
impact. Internal-only renames belong in developer docs, not in the changelog.

## See also

- [DxMessaging Memory Reclamation](./memory-reclamation.md)
- [Memory Reclaim Coverage](./memory-reclaim-coverage.md)
- [Changelog Management](../../changelog-management/references/changelog-management.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-06 | Initial version |
