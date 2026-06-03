---
title: "Banner SVG Conventions"
id: "banner-svg-conventions"
category: "documentation"
version: "1.0.0"
created: "2026-05-08"
updated: "2026-05-08"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/images/DxMessaging-banner.svg"
    - path: "site/images/DxMessaging-banner.svg"
    - path: "scripts/sync-banner-version.ps1"
    - path: "scripts/validate-banner.js"
    - path: ".pre-commit-config.yaml"
    - path: ".github/workflows/validate-banner.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "svg"
  - "branding"
  - "tooling"
  - "policy"
  - "accessibility"

complexity:
  level: "intermediate"
  reasoning: "Geometry, accessibility, and drift-prevention concerns intersect; coordinate math must stay in sync with validator heuristics."

impact:
  performance:
    rating: "none"
    details: "Static asset"
  maintainability:
    rating: "high"
    details: "Drift between the SVG, the sync script, and the validator silently corrupts a flagship asset; the rules below prevent that."
  testability:
    rating: "high"
    details: "scripts/validate-banner.js is the executable specification."

prerequisites:
  - "Familiarity with SVG (viewBox, transforms, gradients, filters)"
  - "Awareness of the project's ASCII-only policy"

dependencies:
  packages: []
  skills:
    - "ascii-only-docs"

applies_to:
  languages:
    - "SVG"
    - "JavaScript"
    - "PowerShell"
  frameworks:
    - "GitHub README rendering"
    - "MkDocs"

aliases:
  - "DxMessaging banner"
  - "README banner SVG"
  - "Banner version sync"

related:
  - "ascii-only-docs"
  - "documentation-style-guide"

status: "stable"
---

# Banner SVG Conventions

> **One-line summary**: The DxMessaging banner SVG is a flagship asset with strict invariants (version-block byte equality with a PowerShell heredoc, badge encapsulation, single-source-of-truth for mutable strings, ASCII source). All invariants are enforced by `scripts/validate-banner.js`, which runs in pre-commit and CI.

## Overview

The canonical banner lives at `docs/images/DxMessaging-banner.svg`. It is the asset rendered in the README; MkDocs copies it to the gitignored `site/` build output for the docs site, so only the `docs/` copy is version-controlled and validated.

A PowerShell sync script (`scripts/sync-banner-version.ps1`) updates the version badge whenever `package.json#version` changes. A Node validator (`scripts/validate-banner.js`) enforces 18 separate invariants. The validator is wired into the pre-commit hook (`.pre-commit-config.yaml`) and CI (`.github/workflows/validate-banner.yml`).

## Why this skill exists

The banner went through 11 iterations of adversarial review to reach production quality. Several classes of defects were found and fixed; the validator codifies the lessons so they cannot regress:

- **Drift**: version (`v3.0.1`), test count (`300+ Tests`), or feature labels duplicated across surfaces (e.g., `<title>`, `<desc>`, comments) without sync.
- **Sync mismatch**: the SVG's version-badge block must be byte-identical to the heredoc in the PowerShell sync script. If it is not, the next sync silently rewrites and corrupts surrounding content.
- **Encapsulation**: stat-badge `<rect>` widths must clear the worst-case rendered text (emoji widths vary 1.5x across renderers).
- **Accessibility**: `<title>` and `<desc>` must be present, both non-empty, and `role="img"` must be on the root `<svg>`.
- **ASCII source**: comments and prose use only ASCII (em-dashes, smart quotes, etc. are forbidden). Numeric character references for emoji (e.g., `&#x1F501;`) are ASCII source and allowed.
- **Layout**: viewBox is always `0 0 800 200`; no element overflows.

## Hard invariants (each enforced by `scripts/validate-banner.js`)

### Sync / drift

1. **Version-badge block matches the PowerShell heredoc** in `scripts/sync-banner-version.ps1`. The script's `$newVersionText` heredoc, with `$version` substituted from `package.json`, must appear verbatim in the SVG. Any deviation (whitespace, attribute ordering, missing/extra `px` unit, line wrapping) means the next version sync will mutate the SVG and destroy surrounding edits.
1. **Banner version matches `package.json#version`** (the version-badge text must contain `vX.Y.Z`).

### Hard requirements

1. **`viewBox="0 0 800 200"`** with `width="800"` and `height="200"`.
1. **No external resources**: no `<image href>`, no remote `xlink:href`, no `@import`, no `url(http*)`.
1. **No JavaScript**: no `<script>` elements, no `on*` event handlers, no `javascript:` schemes.
1. **File size <= 12 KiB.**
1. **ASCII-only source.** Numeric character references (`&#x1F501;`) are allowed because they are ASCII bytes.

### Accessibility

1. **`<title>` and `<desc>` exist** as direct children of root `<svg>` and are non-empty.
1. **`role="img"`** on the root SVG.
1. **`aria-labelledby` / `aria-describedby`** (if present) reference IDs that exist on `<title>` / `<desc>`.

### Layout / encapsulation

1. **Stat-badge encapsulation**: each `<rect>` width is at least the worst-case rendered text width plus 20 px padding. Worst-case text width uses these heuristics: monospace ~0.6em per char, emoji ~1.5em advance, VS-16 (`&#xFE0F;`) is zero-width.
1. **Feature row labels** are exactly four items in this order: `Simple`, `Automatic`, `Dev-Friendly`, then any string matching `^\d+\+ Tests$` (e.g., `300+ Tests`).
1. **All `<rect>` and `<line>` bounding boxes within the viewBox.**

### Code quality

1. **XML well-formed.**
1. **No duplicate `id` attributes.**
1. **Unused `<defs>` IDs** are warned (not failed).

### Drift prevention

1. **`vX.Y.Z` semver appears only inside the version-badge `<text>`.** Forbidden in `<title>`, `<desc>`, and comments.
1. **`N+ Tests` appears only inside the feature-row label.**

## Single-source-of-truth pattern

| Mutable string  | Source of truth                            | Enforced by     |
| --------------- | ------------------------------------------ | --------------- |
| Version         | `package.json#version`                     | sync script     |
| Version display | Version badge `<text>` in both SVGs        | validator (#2)  |
| Test count      | Feature row label (e.g., `300+ Tests`)     | validator (#19) |
| Feature pillars | Feature row labels                         | validator (#13) |
| Studio name     | Bottom-right `<text>` (`Wallstop Studios`) | manual          |

`<title>` and `<desc>` must NOT mention the version, test count, or feature pillars; they are short, fixed prose.

## Working with the banner

### Editing the banner

1. Edit `docs/images/DxMessaging-banner.svg`.
1. Run the validator: `node scripts/validate-banner.js`. Fix any reported errors.
1. Commit. The pre-commit hook will rerun validation.

### Bumping the version

The sync script handles this automatically when `package.json#version` changes (pre-commit hook). Manual run: `pwsh -File scripts/sync-banner-version.ps1`. The script is idempotent: if the SVG already matches `package.json`, no file is touched.

### Restructuring the version badge

If you need to change the version badge's structure (size, position, font, fill, etc.):

1. Update the heredoc in `scripts/sync-banner-version.ps1`.
1. Update the SVG to match.
1. Run `node scripts/validate-banner.js` and verify byte-equality.
1. Run `pwsh -File scripts/sync-banner-version.ps1` and confirm no SVG modification (idempotent).

The heredoc and the SVG block must be character-for-character identical (after `$version` substitution).

### Rewording feature labels

If you change feature pillars (e.g., `300+ Tests` to `500+ Tests`):

1. Edit the feature-row `<text>` in the SVG.
1. Update `scripts/validate-banner.js` if the new label does not match `^\d+\+ Tests$` (or if you change the four-item order).
1. Update this skill file if the meaning has changed.

### Rewording `<title>` or `<desc>`

`<title>` is the SVG's accessible name; keep it concise (`DxMessaging: Unity messaging library`). `<desc>` carries elaboration. Neither may include the version, test count, or stat-badge content (drift risk; enforced by validator #18 and #19).

## Banned patterns

- Hardcoding the version (`v3.0.1`) anywhere except inside the version-badge `<text>`.
- Hardcoding the test count (`300+ Tests`) anywhere except inside the feature-row label.
- Mirroring stat-badge text in `<desc>` (e.g., listing `Unity 2021.3+, Zero Alloc, High Perf`).
- Numeric coordinate assertions in comments (e.g., `Badge 3 bottom y=155`). They rot the moment any geometry changes; rewrite as rule-based language.
- Em-dashes, smart quotes, ellipsis, or any non-ASCII byte in the SVG source.
- `dominant-baseline="central"` on text elements that need cross-renderer stability; use explicit `y` offsets instead.

## Enforcement layers

1. **`scripts/validate-banner.js`** - Node validator, 19 independent checks, reports all errors before exiting. Run via `node scripts/validate-banner.js`.
1. **Pre-commit hook** (`.pre-commit-config.yaml#validate-banner`) - runs the validator when any of the banner files, sync script, validator, or `package.json` are staged.
1. **CI workflow** (`.github/workflows/validate-banner.yml`) - runs the validator on every PR and push to `main`/`master` that touches relevant paths.
1. **Sync hook** (`.pre-commit-config.yaml#sync-banner-version`) - already-existing pre-commit hook that auto-updates the version badge.

## See Also

- [ASCII-Only Documentation Policy](./ascii-only-docs.md)
- [Documentation Style Guide](./documentation-style-guide.md)
